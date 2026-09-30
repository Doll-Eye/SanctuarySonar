import AppKit
import os
import ScreenCaptureKit
import Cartography
import Keel

/// The captured window's size in pixels and how much of its top is title bar, shared by
/// everything that crops a part of the game picture out of it.
struct WindowGeometry {
    let filter: SCContentFilter
    let scale: CGFloat
    let width: Int
    let height: Int
    let titleBar: Int
    let fullScreen: Bool

    init(_ window: SCWindow) {
        filter = SCContentFilter(desktopIndependentWindow: window)
        scale = CGFloat(filter.pointPixelScale)
        width = Int(filter.contentRect.width * scale)
        height = Int(filter.contentRect.height * scale)
        fullScreen = NSScreen.screens.contains { $0.frame.size == window.frame.size }
        // A window has a 28-point title bar (measured on Shadow: 57 px at 2×); full screen
        // has none.
        titleBar = fullScreen ? 0 : Int((28 * scale).rounded())
    }

    /// A stream configuration that captures just this rectangle (window pixels) at its
    /// own pixel size.
    func configuration(for rect: (x: Int, y: Int, width: Int, height: Int), fps: Int32, pixelFormat: OSType) -> SCStreamConfiguration {
        let config = SCStreamConfiguration()
        config.sourceRect = CGRect(x: CGFloat(rect.x) / scale, y: CGFloat(rect.y) / scale,
                                   width: CGFloat(rect.width) / scale, height: CGFloat(rect.height) / scale)
        config.width = rect.width
        config.height = rect.height
        config.minimumFrameInterval = CMTime(value: 1, timescale: fps)
        config.pixelFormat = pixelFormat
        config.queueDepth = 3
        config.showsCursor = false
        return config
    }
}

/// Reads the objective tracker under the minimap once a second and reports the dungeon's
/// objective when it changes.
///
/// Text recognition is noisy on a moving game picture, so a new reading is only believed
/// when two in a row agree. Counts ("Slay the Enraged Spirits: 2") are part of the
/// objective's text but not of its `kind`: a new kind is announced, a count is not — it is
/// there on request.
final class ObjectiveWatcher: NSObject, SCStreamOutput, SCStreamDelegate {
    /// Called on the main queue with each newly settled objective.
    var onObjective: ((Objective) -> Void)?
    /// Called on the main queue when the area name above the minimap changes.
    var onArea: ((String) -> Void)?
    /// Called on the main queue when the objective's count moves ("…: 3" → 2).
    var onCount: ((Int) -> Void)?
    /// Called on the main queue when a countdown in the HUD column changes (the Undercity).
    var onTimer: ((Int) -> Void)?
    /// Called on the main queue when the floor counter changes (the Undercity's "FLOOR 2/3").
    var onFloor: ((Int, Int) -> Void)?
    /// The objective as best read so far, with its count — for reading out on request.
    let current = OSAllocatedUnfairLock<(Objective?, Int?)>(initialState: (nil, nil))
    /// Whether the tracker has said "Undercity" this run (`HUDState.timedRun`).
    let timedRun = OSAllocatedUnfairLock<Bool>(initialState: false)
    /// The agreement rules, shared with MapLab.
    private let hud = HUDState()
    /// Reads in a row that returned no text at all while the minimap was readable. Vision
    /// has failed silently before (26 Sep: no lines, no error, for a whole run); the guide
    /// is told after a minute of it so the owner knows the objective is not being followed.
    private var emptyReads = 0
    var onTextTrouble: (() -> Void)?

    private let queue = DispatchQueue(label: "sanctuarysonar.objective")
    private var stream: SCStream?
    private let running = OSAllocatedUnfairLock(initialState: false)
    /// Set by the guide: false while the minimap is unreadable — the inventory, the map or
    /// a menu is up, and whatever text sits where the tracker was is not an objective.
    let mapVisible = OSAllocatedUnfairLock(initialState: true)

    func start(window: SCWindow) async throws {
        let geometry = WindowGeometry(window)
        let rect = TrackerLayout.hudRect(windowWidth: geometry.width, windowHeight: geometry.height,
                                         titleBar: geometry.titleBar)
        let config = geometry.configuration(for: rect, fps: 1, pixelFormat: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange)
        let stream = SCStream(filter: geometry.filter, configuration: config, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        self.stream = stream
        queue.async { self.hud.reset() }
        current.withLock { $0 = (nil, nil) }
        running.withLock { $0 = true }
        try await stream.startCapture()
        log("Objective watcher: tracker \(rect.width)×\(rect.height) at \(rect.x),\(rect.y)")
    }

    func stop() {
        running.withLock { $0 = false }
        guard let stream else { return }
        self.stream = nil
        Task { try? await stream.stopCapture() }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer buffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, running.withLock({ $0 }), buffer.isValid,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(buffer, createIfNecessary: false)
                as? [[SCStreamFrameInfo: Any]],
              let raw = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: raw) == .complete,
              let pixels = CMSampleBufferGetImageBuffer(buffer) else { return }
        guard mapVisible.withLock({ $0 }) else { return }
        // Luma only. Not prepared with `Gray.textEnhanced`: the objective line is drawn in a
        // dimmer grey than the title, and the preparation left it too faint to read (26 Sep).
        CVPixelBufferLockBaseAddress(pixels, .readOnly)
        var prepared: CGImage?
        if let base = CVPixelBufferGetBaseAddressOfPlane(pixels, 0)?.assumingMemoryBound(to: UInt8.self) {
            let height = CVPixelBufferGetHeightOfPlane(pixels, 0)
            prepared = Gray(lumaPlane: base, rowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 0),
                            rect: (0, 0, CVPixelBufferGetWidthOfPlane(pixels, 0), height))
                .image
        }
        CVPixelBufferUnlockBaseAddress(pixels, .readOnly)
        guard let prepared else { return }
        let lines = TrackerReader.placedLines(in: prepared)
        if lines.isEmpty {
            emptyReads += 1
            if emptyReads == 60 {
                log("Objective text: no lines read for 60 s while the minimap was readable" +
                    (TrackerReader.lastError.map { "; last error \($0)" } ?? "; no error reported"))
                DispatchQueue.main.async { self.onTextTrouble?() }
            }
        } else {
            if emptyReads >= 60 { log("Objective text: reading again") }
            emptyReads = 0
        }
        let changed = hud.update(lines)
        current.withLock { $0 = (hud.objective, hud.count) }
        let timed = hud.timedRun
        let timedBefore = timedRun.withLock { was -> Bool in let b = was; was = timed; return b }
        if timed != timedBefore, let objective = hud.objective { DispatchQueue.main.async { self.onObjective?(objective) } }
        if let area = changed.area { DispatchQueue.main.async { self.onArea?(area) } }
        if let objective = changed.objective { DispatchQueue.main.async { self.onObjective?(objective) } }
        if let count = changed.count { DispatchQueue.main.async { self.onCount?(count) } }
        if let timer = changed.timer { DispatchQueue.main.async { self.onTimer?(timer) } }
        if let floor = changed.floor { DispatchQueue.main.async { self.onFloor?(floor.number, floor.of) } }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        log("Objective watcher stopped by the system: \(error.localizedDescription)")
        running.withLock { $0 = false }
        DispatchQueue.main.async { self.stream = nil }
    }
}
