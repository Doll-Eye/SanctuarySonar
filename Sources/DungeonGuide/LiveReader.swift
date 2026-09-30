import AppKit
import os
import ScreenCaptureKit
import Cartography
import Keel

/// Captures just the minimap of the chosen window, ten times a second, and keeps a stitched
/// map and a route to the nearest unexplored opening.
///
/// The stream is cropped to the minimap on the GPU (`sourceRect`), so only ~150 thousand
/// pixels a frame reach the reader. Everything after capture happens on `queue`, which owns
/// the stitcher and the navigator; results go to the main queue as `Snapshot`s.
final class LiveReader: NSObject, SCStreamOutput, SCStreamDelegate {

    struct Snapshot {
        let legible: Bool
        let contrast: Double
        /// Share of the readable minimap that is floor, and share with busy texture.
        var floorFraction = 0.0
        var busyFraction = 0.0
        let guidance: Guidance?
        /// Direction of travel over the last 0.6 s, if moving.
        let heading: Double?
        /// The character's facing from the minimap arrow, if it could be read.
        var facing: Double? = nil
        /// Bearing of the objective marker pinned to the minimap's edge, if one is showing.
        var markerBearing: Double? = nil
        /// The lead is a beacon or attunement pack because time is short.
        var toBeacon = false
        /// Minimap pixels from the player to the nearest red mark, if any is showing.
        var nearestMark: Double? = nil
        /// What that time target is, for speech: "Beacon" or "Afflicted pack".
        var timeTarget = "Beacon"
        /// The bearing of the side with floor if the way ahead is blocked, from the map; nil
        /// when neither side has any.
        var sidestep: Double? = nil
        /// The map was started again on this frame.
        let newMap: Bool
        /// How long reading, stitching and routing this frame took.
        let milliseconds: Double
        /// Healing wells and arches confirmed on this frame: kind, straight-line bearing
        /// from the player (radians clockwise from up) and distance in minimap pixels.
        var newLandmarks: [(Landmark, Double, Double)] = []
    }

    var onSnapshot: ((Snapshot) -> Void)?
    var onStopped: ((Error?) -> Void)?

    private let queue = DispatchQueue(label: "dungeonguide.reader")
    /// Touched only on the main queue: start and stop never wait on `queue`, which may be
    /// busy with a frame.
    private var stream: SCStream?
    /// Read by the frame handler, so a stop takes effect on the next frame without the main
    /// queue having to wait for the current one.
    private let running = OSAllocatedUnfairLock(initialState: false)
    /// Set by the guide from the objective: lead to red marks while it is to slay.
    /// Set by the guide from the objective and the area: what to lead to, and the area rule.
    let intent = OSAllocatedUnfairLock(initialState: Intent.none)

    /// The area name above the minimap changed.
    func setArea(_ name: String) {
        queue.async { self.stitcher.setArea(name) }
    }
    /// Set by the guide while leading back to the marked spot.
    let leadToSpot = OSAllocatedUnfairLock(initialState: false)
    /// The Undercity's countdown as last read, set by the guide; nil when there is none.
    let timeLeft = OSAllocatedUnfairLock<Int?>(initialState: nil)
    /// Under this many seconds, a beacon or attunement pack in view is led to before the
    /// objective: each one lit bought about fifty seconds on 27 Sep, and the second floor
    /// began with 51 and ended with none lit.
    static let shortTime = 40
    /// Whether beacons (blue icons) are time targets at all. Off: afflicted packs only — the
    /// owner cannot light them by feel (28 Sep 2026, Windows: three beacon leads on one floor,
    /// two reached, no time gained). The guide sets it from its setting.
    nonisolated(unsafe) static var leadToBeacons = UserDefaults.standard.bool(forKey: "leadToBeacons")

    /// Marks the player's position; `done` gets whether there was one, on the main queue.
    func markSpot(_ done: @escaping (Bool) -> Void) {
        queue.async {
            let marked = self.stitcher.markSpot()
            DispatchQueue.main.async { done(marked) }
        }
    }

    /// What the map knows, relative to the player, for reading out: landmarks, the marked
    /// spot, remembered marks. `done` on the main queue.
    func describe(_ done: @escaping (String) -> Void) {
        queue.async {
            var parts: [String] = []
            if let player = self.stitcher.player {
                func place(_ p: CGPoint) -> String {
                    let bearing = atan2(p.x - player.x, -(p.y - player.y))
                    return "\(Compass.word(bearing)), \(Guide.howFar(hypot(p.x - player.x, p.y - player.y)))"
                }
                let landmarks = Self.merged(self.stitcher.landmarkMemory.landmarks)
                    .sorted { hypot($0.point.x - player.x, $0.point.y - player.y) < hypot($1.point.x - player.x, $1.point.y - player.y) }
                for l in landmarks { parts.append("\(l.kind.rawValue) \(place(l.point))") }
                if landmarks.isEmpty { parts.append("No healing well or arch seen yet") }
                if let spot = self.stitcher.spot { parts.append("Marked spot \(place(spot))") }
                let marks = self.stitcher.markMemory.marks.count
                if marks > 0 { parts.append("\(marks) marked enem\(marks == 1 ? "y" : "ies")") }
                // Where the unexplored ground is: the lead when it is an opening, and the rest.
                if let b = self.markerBearing { parts.append("Objective marker \(Compass.word(b)), beyond the map") }
                if let g = self.lastGuidance {
                    let lead = (g.toMark || g.toSpot || g.toArea) ? [] : [(g.bearing, g.distance)]
                    let words = Guide.directions(lead + g.others)
                    parts.append(words.isEmpty ? "No unexplored openings in sight" : "Unexplored: " + words.joined(separator: "; "))
                }
                if self.stitcher.areaNames.count > 1 { parts.append("Areas seen: " + self.stitcher.areaNames.joined(separator: ", ")) }
            }
            let text = parts.isEmpty ? "Nothing known on the map yet." : parts.joined(separator: ". ") + "."
            DispatchQueue.main.async { done(text) }
        }
    }

    /// One entry per landmark: the map drifts over a long run and the same well is
    /// confirmed again a little way from where it was (four entries 90 px apart on 26 Sep).
    static func merged(_ landmarks: [(kind: Landmark, point: CGPoint)]) -> [(kind: Landmark, point: CGPoint)] {
        var kept: [(kind: Landmark, point: CGPoint)] = []
        for l in landmarks where !kept.contains(where: { $0.kind == l.kind && hypot($0.point.x - l.point.x, $0.point.y - l.point.y) < (l.kind == .healingWell ? 150 : 60) }) {
            kept.append(l)
        }
        return kept
    }

    /// The guide found the player blocked while pushing along `facing`: the map learns a wall there.
    func blocked(facing: Double) {
        queue.async { self.stitcher.markBlocked(facing: facing) }
    }

    /// Whether a spot is marked on the current map; `done` on the main queue.
    func hasSpot(_ done: @escaping (Bool) -> Void) {
        queue.async {
            let has = self.stitcher.spot != nil
            DispatchQueue.main.async { done(has) }
        }
    }
    private let stitcher = Stitcher()
    private let navigator = Navigator()
    private var refusedSince: Double?
    /// The last plan, for describing where the unexplored ground is.
    private var lastGuidance: Guidance?
    /// Where the minimap box sits inside the captured rectangle, and its size.
    private var inset = (x: 0, y: 0)
    private var box = (width: 0, height: 0)
    /// The objective marker's bearing and when it was last seen; held for `markerHold`.
    private var markerBearing: Double?
    private var markerSeen = -1.0
    private var markerNearUntil = -1.0
    /// The time target being led to, on the canvas: a pack's hourglasses move and split, and
    /// the nearest one changed every second (floor 1 of the 08:15 run: 10 reversals against
    /// 3 with the marker alone). Kept while any icon is within `timeTargetHold` of it.
    private var lastTimeTarget: CGPoint?
    static let timeTargetHold = 50.0
    /// Time targets reached (the player came within `reached` px): a lit beacon keeps its
    /// icon, and on the 09:16 run the guide led to the one just lit, at the player's feet,
    /// for minutes — 44 reversals on floor 1. Nothing within `doneRadius` of one counts again.
    private var doneTargets: [CGPoint] = []
    static let reached = 35.0
    static let doneRadius = 120.0
    /// A time target's route may be this many times its straight distance, or 250 px, at
    /// most; a detour longer than that is not worth the time it buys.
    static let detourRatio = 2.5
    static let detourCap = 250.0
    /// An afflicted pack is a target only within this straight distance (they come to you);
    /// under `shortTime` any distance counts. (The 10:45 cut-back to "inside the box and
    /// within 100 px only" was reverted at 11:20 with the rest of that afternoon's changes:
    /// restore the state that reached floor 3 three times, then change one thing at a time.)
    static let packReach = 120.0
    static let markerHold = 3.0
    /// The last frame and what was read from it, kept for `saveMap`.
    private var lastFrame: (Gray, MinimapReading)?
    private var startedAt = CACurrentMediaTime()

    /// A legible minimap that fits nowhere on the map for this long is somewhere new — a
    /// new dungeon, a new floor, a teleport — and gets a new map.
    static let newMapAfter = 2.5
    static let framesPerSecond: Int32 = 10

    /// Starts reading. Returns a description of where the minimap was looked for.
    func start(window: SCWindow) async throws -> String {
        let geometry = WindowGeometry(window)
        let rect = MinimapLayout.rect(windowWidth: geometry.width, windowHeight: geometry.height,
                                      titleBar: geometry.titleBar)
        let (outer, inset) = MinimapLayout.outer(rect, windowWidth: geometry.width, windowHeight: geometry.height)
        self.inset = inset
        self.box = (rect.width, rect.height)
        let config = geometry.configuration(for: outer, fps: Self.framesPerSecond,
                                            pixelFormat: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange)
        let stream = SCStream(filter: geometry.filter, configuration: config, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        self.stream = stream
        running.withLock { $0 = true }
        queue.async {
            self.stitcher.reset()
            self.navigator.forget()
            self.refusedSince = nil
            self.lastGuidance = nil
            self.markerBearing = nil
            self.lastTimeTarget = nil
            self.doneTargets = []
            self.startedAt = CACurrentMediaTime()
        }
        try await stream.startCapture()
        return "window \(geometry.width)×\(geometry.height), \(geometry.fullScreen ? "full screen" : "title bar \(geometry.titleBar) px"), minimap \(rect.width)×\(rect.height) at \(rect.x),\(rect.y)"
    }

    func stop() {
        running.withLock { $0 = false }
        guard let stream else { return }
        self.stream = nil
        Task { try? await stream.stopCapture() }
    }

    /// Throws the map away and starts again from the next frame.
    func newMap() {
        queue.async {
            self.stitcher.reset()
            self.navigator.forget()
            self.refusedSince = nil
            self.lastTimeTarget = nil
            self.doneTargets = []
            self.markerBearing = nil
        }
    }

    /// Writes the current map, with the route, as a PNG, and beside it the last minimap as
    /// the reader saw it (`-minimap.png`). For checking from outside.
    func saveMap(to url: URL) {
        queue.async {
            if let (gray, reading) = self.lastFrame, let image = Picture.overlay(gray, reading) {
                let minimapURL = url.deletingPathExtension().appendingPathExtension("minimap.png")
                Picture.write(image, to: minimapURL)
                log(String(format: "Last minimap: contrast %.1f, floor %.0f%%, arrow %@ → %@", reading.contrast,
                           reading.floorFraction * 100, reading.arrow == nil ? "none" : "found", minimapURL.path))
            }
            let guidance = self.navigator.plan(self.stitcher)
            if let image = self.stitcher.picture(path: guidance?.path ?? [], target: guidance?.target),
               Picture.write(image, to: url) {
                log("Map saved: \(url.path)")
            }
        }
    }

    // MARK: Frames

    func stream(_ stream: SCStream, didOutputSampleBuffer buffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, running.withLock({ $0 }), buffer.isValid,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(buffer, createIfNecessary: false)
                as? [[SCStreamFrameInfo: Any]],
              let raw = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: raw) == .complete,
              let pixels = CMSampleBufferGetImageBuffer(buffer) else { return }

        CVPixelBufferLockBaseAddress(pixels, .readOnly)
        let width = CVPixelBufferGetWidthOfPlane(pixels, 0), height = CVPixelBufferGetHeightOfPlane(pixels, 0)
        guard let base = CVPixelBufferGetBaseAddressOfPlane(pixels, 0)?.assumingMemoryBound(to: UInt8.self) else {
            CVPixelBufferUnlockBaseAddress(pixels, .readOnly)
            return
        }
        // The capture is the minimap box plus a margin for edge-pinned icons; the reader
        // and the marks see the box, the marker finder the whole capture.
        let boxRect = (inset.x, inset.y, min(box.width, width - inset.x), min(box.height, height - inset.y))
        let gray = Gray(lumaPlane: base, rowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 0), rect: boxRect)
        var marks: [CGPoint] = []
        var marker: CGPoint?
        var blue: [CGPoint] = []
        var orange: [CGPoint] = []
        let wantNow = intent.withLock { $0 }
        let short = wantNow.timed && (timeLeft.withLock { $0 }).map { $0 <= Self.shortTime } == true
        // In a timed run the blue icons are always looked for: a beacon on the minimap is a
        // detour worth taking whatever the clock (the 08:39 runs walked past one for fifteen
        // seconds with the marker 300 px off, and arrived on floor 2 with 70 s).
        if let chroma = CVPixelBufferGetBaseAddressOfPlane(pixels, 1)?.assumingMemoryBound(to: UInt8.self) {
            marks = RedMarks.find(cbcr: chroma, rowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 1), lumaRect: boxRect)
            marker = ObjectiveMarker.find(luma: base, lumaRowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 0),
                                          cbcr: chroma, cbcrRowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 1),
                                          outer: (0, 0, width, height), inset: inset)
            if wantNow.timed {
                blue = BlueIcons.find(luma: base, lumaRowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 0),
                                      cbcr: chroma, cbcrRowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 1),
                                      outer: (0, 0, width, height), inset: inset)
                orange = OrangeIcons.find(luma: base, lumaRowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 0),
                                          cbcr: chroma, cbcrRowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 1),
                                          outer: (0, 0, width, height), inset: inset)
            }
        }
        CVPixelBufferUnlockBaseAddress(pixels, .readOnly)

        let began = CACurrentMediaTime()
        let time = began - startedAt
        var reading = MinimapReader.read(gray)
        reading.marks = marks
        lastFrame = (gray, reading)
        // Short on time with a beacon in sight: the beacon is the target instead.
        // …unless the objective marker is itself on the minimap: the boss room in reach
        // beats a beacon (floor 1 of the 08:00 run was won with 40 s left that way).
        var beaconLead = false
        var timeTarget = "Beacon"
        let objectiveMarker = marker
        let inset = 40.0
        if marker.map({ $0.x > inset && $0.y > inset && $0.x < Double(box.width) - inset && $0.y < Double(box.height) - inset }) == true {
            markerNearUntil = time + Self.markerHold     // it jitters in and out of the inset
        }
        let markerNear = time < markerNearUntil
        if !markerNear, let arrow = reading.arrow {
            // A beacon inside the box is near enough to take at any time; a pinned one only
            // when the clock is short.
            // Beacons and afflicted packs alike: both buy time. Orange packs pin to the edge too.
            let beacons: [(CGPoint, String)] = Self.leadToBeacons ? blue.map { ($0, "Beacon") } : []
            let all = beacons + orange.map { ($0, "Afflicted pack") }
            let inside = all.filter { $0.0.x > 20 && $0.0.y > 20 && $0.0.x < Double(box.width) - 20 && $0.0.y < Double(box.height) - 20 }
            var candidates = short ? all : inside
            if let player = stitcher.player {
                let canvas: (CGPoint) -> CGPoint = { CGPoint(x: player.x + $0.x - arrow.x, y: player.y + $0.y - arrow.y) }
                // Reached: the current target with the player on top of it is done.
                if let last = lastTimeTarget, hypot(last.x - player.x, last.y - player.y) < Self.reached {
                    doneTargets.append(last)
                    lastTimeTarget = nil
                }
                candidates = candidates.filter { c in
                    let p = canvas(c.0)
                    if doneTargets.contains(where: { hypot($0.x - p.x, $0.y - p.y) < Self.doneRadius }) { return false }
                    if hypot(c.0.x - arrow.x, c.0.y - arrow.y) < Self.reached { return false }
                    if c.1 != "Beacon", !short, hypot(c.0.x - arrow.x, c.0.y - arrow.y) > Self.packReach { return false }
                    return true
                }
            }
            // The one already being led to, if it is still about; else the nearest.
            var chosen: (CGPoint, String)?
            if let last = lastTimeTarget, let player = stitcher.player {
                chosen = candidates.filter { hypot(player.x + $0.0.x - arrow.x - last.x, player.y + $0.0.y - arrow.y - last.y) < Self.timeTargetHold }
                    .min { hypot($0.0.x - arrow.x, $0.0.y - arrow.y) < hypot($1.0.x - arrow.x, $1.0.y - arrow.y) }
            }
            if chosen == nil {
                chosen = candidates.min(by: { hypot($0.0.x - arrow.x, $0.0.y - arrow.y) < hypot($1.0.x - arrow.x, $1.0.y - arrow.y) })
            }
            if let chosen {
                marker = chosen.0
                beaconLead = true
                timeTarget = chosen.1
                if let player = stitcher.player { lastTimeTarget = CGPoint(x: player.x + chosen.0.x - arrow.x, y: player.y + chosen.0.y - arrow.y) }
            } else {
                lastTimeTarget = nil
            }
        }
        // The cyan objective marker is an Undercity thing. In ordinary dungeons the only cyan
        // seen so far is a chest icon at the entrance (Forbidden City, 27 Sep replay), which
        // pulled the lead for the first minute; until a recording shows a real one, none.
        if !wantNow.timed { marker = nil }
        var markerInBox: CGPoint?
        if let marker, let arrow = reading.arrow {
            markerBearing = atan2(marker.x - arrow.x, -(marker.y - arrow.y))
            markerSeen = time
            // Well inside the box, it is a place on the map, not a direction.
            // 40 px: the pinned icon, ring and all, sits up to ~20 px inside the border.
            if marker.x > inset, marker.y > inset, marker.x < Double(box.width) - inset, marker.y < Double(box.height) - inset {
                markerInBox = marker
            }
        } else if markerBearing != nil, time - markerSeen > Self.markerHold {
            markerBearing = nil
        }
        guard reading.isLegible else {
            publish(Snapshot(legible: false, contrast: reading.contrast, floorFraction: reading.floorFraction,
                             busyFraction: reading.busyFraction, guidance: nil, heading: nil, newMap: false,
                             milliseconds: (CACurrentMediaTime() - began) * 1000))
            return
        }
        var newMap = false
        let placement = stitcher.add(reading, time: time)
        if placement.placed {
            refusedSince = nil
        } else if let since = refusedSince {
            if time - since > Self.newMapAfter {
                log(String(format: "Minimap fits nowhere for %.1f s (best score %.2f; floor %.0f %%, busy %.2f, contrast %.1f): new map",
                           Self.newMapAfter, placement.score, reading.floorFraction * 100, reading.busyFraction, reading.contrast))
                stitcher.reset()
                navigator.forget()
                _ = stitcher.add(reading, time: time)
                refusedSince = nil
                newMap = true
            }
        } else {
            refusedSince = time
        }
        let want = intent.withLock { $0 }
        var rule = Navigator.AreaRule.none
        if let s = want.stayIn, let i = stitcher.areaIndex(s) { rule = .stay(i) }
        if let l = want.leave, let i = stitcher.areaIndex(l) { rule = .leave(i) }
        // "Travel to the Siren's Chamber" when the Siren's Chamber has been walked through:
        // straight there. On 26 Sep the guide looked for new ground instead, for three
        // minutes, with the border 200 px away.
        if let d = want.destination, let i = stitcher.areaMatching(d), i != stitcher.currentArea {
            rule = .goTo(area: i, from: want.leave.flatMap { stitcher.areaIndex($0) })
        }
        // The frame sits on the canvas with its arrow at the player, so a point in the box
        // is the player plus its offset from the arrow.
        var markerPoint: CGPoint?
        if let m = markerInBox, let a = reading.arrow, let player = stitcher.player {
            markerPoint = CGPoint(x: player.x + m.x - a.x, y: player.y + m.y - a.y)
        }
        var guidance = navigator.plan(stitcher, toMarks: want.slay, toSpot: leadToSpot.withLock { $0 },
                                      toArches: want.travel && !want.timed, areaRule: rule, marker: markerBearing,
                                      markerPoint: markerPoint, timed: want.timed)
        // A time target whose route is a long way round is not worth it: plan again for the
        // objective instead (the 09:16 run: a beacon 60 px away across a wall, 424 px of route).
        if beaconLead, let g = guidance, let m = markerPoint ?? markerInBox, let player = stitcher.player {
            var straight = 150.0
            if markerInBox != nil, let a = reading.arrow { straight = Double(hypot(m.x - a.x, m.y - a.y)) }
            _ = player
            if g.distance > max(Self.detourCap, Self.detourRatio * straight) {
                beaconLead = false
                lastTimeTarget = nil
                let objective: Double? = objectiveMarker.flatMap { o in reading.arrow.map { Double(atan2(o.x - $0.x, -(o.y - $0.y))) } }
                guidance = navigator.plan(stitcher, toMarks: want.slay, toSpot: leadToSpot.withLock { $0 },
                                          toArches: want.travel && !want.timed, areaRule: rule, marker: objective, markerPoint: nil, timed: want.timed)
            }
        }
        lastGuidance = guidance
        var landmarks: [(Landmark, Double, Double)] = []
        if placement.placed, let player = stitcher.player {
            landmarks = stitcher.newLandmarks.map { kind, p in
                (kind, atan2(p.x - player.x, -(p.y - player.y)), hypot(p.x - player.x, p.y - player.y))
            }
        }
        publish(Snapshot(legible: true, contrast: reading.contrast, floorFraction: reading.floorFraction,
                         busyFraction: reading.busyFraction, guidance: guidance,
                         heading: stitcher.heading(), facing: reading.facing, markerBearing: markerBearing, toBeacon: beaconLead,
                         nearestMark: reading.arrow.flatMap { a in reading.marks.map { hypot($0.x - a.x, $0.y - a.y) }.min() },
                         timeTarget: timeTarget,
                         sidestep: reading.facing.flatMap { stitcher.sidestep(facing: $0) }, newMap: newMap,
                         milliseconds: (CACurrentMediaTime() - began) * 1000, newLandmarks: landmarks))
    }

    private func publish(_ snapshot: Snapshot) {
        DispatchQueue.main.async { self.onSnapshot?(snapshot) }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        log("Guide capture stopped by the system: \(error.localizedDescription)")
        running.withLock { $0 = false }
        DispatchQueue.main.async { self.stream = nil; self.onStopped?(error) }
    }
}
