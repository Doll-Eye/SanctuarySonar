import AppKit
import os
import ScreenCaptureKit
import Cartography
import Keel

/// Notices the full dungeon map being opened, and reads its area list.
///
/// The owner, 26 Sep 2026: "when I say describe the map, I mean the one I pull up, the full
/// dungeon map, not the overhead one." The full map covers the minimap, so it is looked for
/// only while the minimap is unreadable (`armed`), once a second, in a half-size picture of
/// the whole window: its bottom bar reads "Center on Player … Pan … Zoom … Pin Location", and
/// its left panel lists the dungeon's areas under the dungeon's name in capitals ("FORBIDDEN
/// CITY" / "Path of Blood" / "Ghastly Depths" / "Unexplored Areas"). Reported once per
/// opening; closing the map (the minimap readable again) re-arms it.
final class MapScreenWatcher: NSObject, SCStreamOutput, SCStreamDelegate {
    /// Called on the main queue with the area names read from the panel, when the map opens.
    var onMapOpened: (([String]) -> Void)?

    private let queue = DispatchQueue(label: "sanctuarysonar.mapscreen")
    private var stream: SCStream?
    private let running = OSAllocatedUnfairLock(initialState: false)
    /// True while the minimap is unreadable — the only time the map can be up.
    let armed = OSAllocatedUnfairLock(initialState: false)
    private var reported = false
    /// Frames looked at since arming: the first two that are not the map are logged, so a
    /// map that goes unnoticed leaves a trace (the 26 Sep run: opened twice, nothing logged).
    private var checks = 0

    func start(window: SCWindow) async throws {
        let geometry = WindowGeometry(window)
        let config = SCStreamConfiguration()
        config.width = geometry.width / 2
        config.height = geometry.height / 2
        config.minimumFrameInterval = CMTime(value: 1, timescale: 1)
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.queueDepth = 3
        config.showsCursor = false
        let stream = SCStream(filter: geometry.filter, configuration: config, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        self.stream = stream
        queue.async { self.reported = false; self.checks = 0 }
        running.withLock { $0 = true }
        try await stream.startCapture()
        log("Map-screen watcher: \(config.width)×\(config.height), once a second while the minimap is covered")
    }

    func stop() {
        running.withLock { $0 = false }
        guard let stream else { return }
        self.stream = nil
        Task { try? await stream.stopCapture() }
    }

    /// The minimap is readable again: the map, if it was up, is closed.
    func disarm() {
        armed.withLock { $0 = false }
        queue.async { self.reported = false; self.checks = 0 }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer buffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, running.withLock({ $0 }), armed.withLock({ $0 }), !reported, buffer.isValid,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(buffer, createIfNecessary: false)
                as? [[SCStreamFrameInfo: Any]],
              let raw = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: raw) == .complete,
              let pixels = CMSampleBufferGetImageBuffer(buffer) else { return }
        let lines = TrackerReader.placedLinesXY(in: pixels)
        var screen = MapScreen.read(lines)
        // A second signal that needs no text: the MAP tab's red box at the top of the screen
        // (27 Sep 2026, live: the bar read as "CerteT(thPkn)tT • 7M • O Ck*e" and three words
        // of ten was not the map). Two words and the red tab is.
        let redTab = Self.redTabFraction(pixels)
        if !screen.isMap, redTab >= Self.redTabMinimum, MapScreen.promptWordCount(screen.bar) >= 2 {
            screen = MapScreen.read(lines, force: true)
        }
        checks += 1
        guard screen.isMap else {
            if checks <= 2 {
                log(String(format: "Map-screen check %d: %d lines, red tab %.2f, bar \"%@\" — not the map",
                           checks, lines.count, redTab, String(screen.bar.prefix(80))))
            }
            return
        }
        reported = true
        log(String(format: "Full map open; areas: %@; red tab %.2f; bar \"%@\"", screen.areas.joined(separator: ", "), redTab, String(screen.bar.prefix(80))))
        DispatchQueue.main.async { self.onMapOpened?(screen.areas) }
    }

    /// The MAP tab's red box: x 0.352–0.410 of the window, y 0.047–0.081 with a title bar
    /// (0.014–0.049 full screen); measured 27 Sep 2026 at mean RGB (103, 50, 49), 64 % of
    /// the region "reddish" (R ≥ 70, R ≥ G + 30, R ≥ B + 30) on three map frames, 0 % on game
    /// frames. The band here covers both title-bar cases, so the share is lower.
    static let redTabMinimum = 0.2
    static func redTabFraction(_ pixels: CVPixelBuffer) -> Double {
        guard CVPixelBufferGetPixelFormatType(pixels) == kCVPixelFormatType_32BGRA else { return 0 }
        CVPixelBufferLockBaseAddress(pixels, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(pixels, .readOnly) }
        guard let base = CVPixelBufferGetBaseAddress(pixels)?.assumingMemoryBound(to: UInt8.self) else { return 0 }
        let w = CVPixelBufferGetWidth(pixels), h = CVPixelBufferGetHeight(pixels), rowBytes = CVPixelBufferGetBytesPerRow(pixels)
        let x0 = Int(0.352 * Double(w)), x1 = Int(0.410 * Double(w)), y0 = Int(0.012 * Double(h)), y1 = Int(0.085 * Double(h))
        var reddish = 0, total = 0
        for y in stride(from: y0, to: y1, by: 2) {
            let row = base + y * rowBytes
            for x in stride(from: x0, to: x1, by: 2) {
                let b = Int(row[x * 4]), g = Int(row[x * 4 + 1]), r = Int(row[x * 4 + 2])
                total += 1
                if r >= 70 && r >= g + 30 && r >= b + 30 { reddish += 1 }
            }
        }
        return total > 0 ? Double(reddish) / Double(total) : 0
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        log("Map-screen watcher stopped by the system: \(error.localizedDescription)")
        running.withLock { $0 = false }
        DispatchQueue.main.async { self.stream = nil }
    }
}
