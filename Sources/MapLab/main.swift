import AppKit
import AVFoundation
import CoreGraphics
import Foundation
import Cartography

// MapLab: runs the minimap reader and the navigator over a recording and writes what it saw.
//
//     MapLab <recording.mov> <output folder> [title bar pixels, default 57]
//
// Writes, every 20 seconds of video, map-NNN.png (the stitched dungeon so far: trail red,
// position green, route yellow, the opening being led to cyan) and frame-NNN.png (the
// minimap with floor green, ignored blue, arrow red), and prints one line per 0.2 s sample.
// Offline on purpose: the same recording gives the same answer, so a change to the reader
// can be judged against the last one.

let arguments = CommandLine.arguments
if arguments.count >= 3 && arguments[1] == "--text" {
    try await TextLab.run(URL(fileURLWithPath: arguments[2]), titleBar: 57)
    exit(0)
}
// MapLab --ocr <png>…: the tracker column's text as the accurate and the fast recogniser read
// it — the test for whether accurate recognition is working on this Mac right now.
if arguments.count >= 3 && arguments[1] == "--ocr" {
    for path in arguments.dropFirst(2) {
        guard let image = NSImage(contentsOfFile: path)?.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
            print("no image at \(path)"); continue
        }
        let r = TrackerLayout.rect(windowWidth: image.width, windowHeight: image.height, titleBar: 0)
        let crop = image.cropping(to: CGRect(x: r.x, y: r.y, width: r.width, height: r.height)) ?? image
        for fast in [false, true] {
            TrackerReader.preferFast = fast
            let began = Date()
            let lines = TrackerReader.placedLinesXY(in: crop)
            print(String(format: "%@ (%.1f s): ", fast ? "fast" : "accurate", Date().timeIntervalSince(began))
                  + lines.sorted { $0.y < $1.y }.map { String(format: "%.2f %@", $0.y, $0.text) }.joined(separator: " | "))
            if let error = TrackerReader.lastError { print("  last error: \(error)"); TrackerReader.lastError = nil }
        }
    }
    exit(0)
}
// MapLab --screen <png>…: is this picture of the window the full dungeon map, and what
// areas does its panel list — with the accurate recogniser and with the fast fallback.
if arguments.count >= 3 && arguments[1] == "--screen" {
    for path in arguments.dropFirst(2) {
        guard let image = NSImage(contentsOfFile: path)?.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
            print("no image at \(path)"); continue
        }
        for fast in [false, true] {
            TrackerReader.preferFast = fast
            let lines = TrackerReader.placedLinesXY(in: image)
            let screen = MapScreen.read(lines)
            print("\(path) \(fast ? "fast" : "accurate"): map \(screen.isMap), areas \(screen.areas), bar \"\(screen.bar)\"")
        }
    }
    exit(0)
}
guard arguments.count >= 3 else {
    print("usage: MapLab <recording.mov> <output folder> [title bar pixels]")
    exit(2)
}
let videoURL = URL(fileURLWithPath: arguments[1])
let outURL = URL(fileURLWithPath: arguments[2], isDirectory: true)
let titleBar = arguments.count > 3 ? Int(arguments[3]) ?? 57 : 57
// A fifth argument shrinks the canvas, to exercise re-centring on a short recording.
let canvas = arguments.count > 4 ? Int(arguments[4]) ?? 3000 : 3000
let interval = 0.2
// MAPLAB_BUSY=0.42 raises the busy-pixel limit for recordings whose compression textures the fog.
if let busy = Double(ProcessInfo.processInfo.environment["MAPLAB_BUSY"] ?? "") { MinimapReading.busyLimit = busy }
// MAPLAB_EVERY=2 writes pictures every 2 s instead of every 20.
let pictureEvery = Double(ProcessInfo.processInfo.environment["MAPLAB_EVERY"] ?? "") ?? 20
// MAPLAB_MARKS=1 leads to red marks when there are any, as the live guide does while the
// objective is to slay something.
let leadToMarks = ProcessInfo.processInfo.environment["MAPLAB_MARKS"] == "1"
// MAPLAB_ARCHES=1 leads to unvisited arches, as the live guide does while travelling.
let leadToArches = ProcessInfo.processInfo.environment["MAPLAB_ARCHES"] == "1"
// MAPLAB_BEACONS=1 makes beacons time targets, as the live guide's switch does; off by default.
let leadToBeacons = ProcessInfo.processInfo.environment["MAPLAB_BEACONS"] == "1"
// MAPLAB_SPOT=60,200 marks a spot at 60 s of video and leads back to it from 200 s.
let spotTimes = (ProcessInfo.processInfo.environment["MAPLAB_SPOT"] ?? "").split(separator: ",").compactMap { Double($0) }
var spotMarked = false
// MAPLAB_INTENT=1 reads the area name and the objective once a second of video and applies
// the objective's intent (slay, travel, stay, leave) as the live guide does.
let followIntent = ProcessInfo.processInfo.environment["MAPLAB_INTENT"] == "1"
if ProcessInfo.processInfo.environment["MAPLAB_DEBUG"] == "1", let stages = try? TrackerReader.request().supportedComputeStageDevices {
    print("text stages: " + stages.map { "\($0.key.rawValue): \($0.value)" }.joined(separator: "; "))
}
// MAPLAB_UNTIL=30 stops after 30 s of video.
let until = Double(ProcessInfo.processInfo.environment["MAPLAB_UNTIL"] ?? "") ?? .infinity
var nextText = 0.0
let hud = HUDState()
var intent = Intent(slay: false, travel: false, stayIn: nil, leave: nil)
try FileManager.default.createDirectory(at: outURL, withIntermediateDirectories: true)

let asset = AVURLAsset(url: videoURL)
guard let track = try await asset.loadTracks(withMediaType: .video).first else {
    print("no video track"); exit(1)
}
let reader = try AVAssetReader(asset: asset)
// Luma only: the reader works on brightness, and plane 0 of 420v is exactly that.
let output = AVAssetReaderTrackOutput(track: track, outputSettings: [
    kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
])
reader.add(output)
reader.startReading()

let stitcher = Stitcher(size: canvas)
let navigator = Navigator()
if let from = Double(ProcessInfo.processInfo.environment["MAPLAB_CANDIDATES_FROM"] ?? "") {
    // Prints every plan's candidates from this time on (once a second of video).
    var last = -1.0
    var cropped = false
    navigator.debug = { text in
        let step = Double(ProcessInfo.processInfo.environment["MAPLAB_CANDIDATES_STEP"] ?? "") ?? 1
        if nextSample >= from, nextSample - last >= step { last = nextSample; print(String(format: "%7.2f s  CANDIDATES %@", nextSample, text)) }
        // Once: the canvas round each healing well, with the visited area tinted red.
        if nextSample >= from, !cropped {
            cropped = true
            for (i, well) in stitcher.landmarkMemory.landmarks.filter({ $0.kind == .healingWell }).enumerated() {
                let region = CGRect(x: well.point.x - 300, y: well.point.y - 300, width: 600, height: 600)
                if let image = stitcher.picture(region: region) {
                    Picture.write(image, to: outURL.appendingPathComponent("well-\(i).png"))
                }
            }
        }
    }
}
var nextSample = 0.0, nextPicture = 0.0
let hudLog: FileHandle? = {
    guard let path = ProcessInfo.processInfo.environment["MAPLAB_HUDLOG"] else { return nil }
    FileManager.default.createFile(atPath: path, contents: nil)
    return FileHandle(forWritingAtPath: path)
}()
var samples = 0, placed = 0, illegible = 0, pictureIndex = 0
var lastGuidance: Guidance?
// As the live guide: a legible minimap that fits nowhere for 2.5 s is a new map (the
// Undercity replay of 27 Sep stitched the Helltide's minimap first and refused every frame after).
var refusedSince: Double?
var floorSeen: Int?
// Nothing is stitched for this long after a floor change: the fade frames seeded phantom floor (30 Sep 2026).
var settleUntil = -1.0
var beaconSaid = -10.0
var blueSaid = -10.0
var timeIcons: [(CGPoint, String)] = []
var lastTimeTarget: CGPoint?
var objectiveSaid = -10.0
var doneTargets: [CGPoint] = []
var markerBearing: Double?
var markerSeen = -1.0, markerSaid = -10.0

while let buffer = output.copyNextSampleBuffer() {
    let time = buffer.presentationTimeStamp.seconds
    if time > until { break }
    guard time >= nextSample, let pixels = CMSampleBufferGetImageBuffer(buffer) else { continue }
    nextSample = time + interval

    CVPixelBufferLockBaseAddress(pixels, .readOnly)
    let rect = MinimapLayout.rect(windowWidth: CVPixelBufferGetWidthOfPlane(pixels, 0),
                                  windowHeight: CVPixelBufferGetHeightOfPlane(pixels, 0),
                                  titleBar: titleBar)
    let base = CVPixelBufferGetBaseAddressOfPlane(pixels, 0)!.assumingMemoryBound(to: UInt8.self)
    let gray = Gray(lumaPlane: base, rowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 0), rect: rect)
    if followIntent, time >= nextText, MinimapReader.read(gray).isLegible {
        nextText = time + 1
        let column = TrackerLayout.hudRect(windowWidth: CVPixelBufferGetWidthOfPlane(pixels, 0),
                                        windowHeight: CVPixelBufferGetHeightOfPlane(pixels, 0), titleBar: titleBar)
        if let image = Gray(lumaPlane: base, rowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 0), rect: column).image {
            let lines = TrackerReader.placedLines(in: image)
            // MAPLAB_HUDLOG=<file>: every HUD reading, tab-separated "time y text", one line per
            // text line, a line "time\t-" for a read with none — so the C# MapLab, which has no
            // OCR on a Mac, can replay the same readings at the same times.
            if let hudLog {
                let stamp = String(format: "%.2f", time)
                let text = lines.isEmpty ? "\(stamp)\t-\n" : lines.map { String(format: "%@\t%.4f\t%@", stamp, $0.y, $0.text.replacingOccurrences(of: "\t", with: " ").replacingOccurrences(of: "\n", with: " ")) }.joined(separator: "\n") + "\n"
                hudLog.write(text.data(using: .utf8)!)
            }
            if ProcessInfo.processInfo.environment["MAPLAB_DEBUG"] == "1" {
                Picture.write(image, to: outURL.appendingPathComponent(String(format: "hud-%.0f.png", time)))
                print("error: \(String(describing: TrackerReader.lastError))")
                print(String(format: "%7.2f s  TEXT %d lines: ", time, lines.count)
                      + lines.map { String(format: "%.2f %@", $0.y, $0.text) }.joined(separator: " | "))
            }
            let changed = hud.update(lines)
            if let a = changed.area {
                stitcher.setArea(a)
                print(String(format: "%7.2f s  AREA %@", time, a))
            }
            if let o = changed.objective { print(String(format: "%7.2f s  OBJECTIVE %@", time, o.text)) }
            if let n = changed.count { print(String(format: "%7.2f s  COUNT %d (%@)", time, n, hud.objective?.kind ?? "")) }
            if let t = changed.timer { print(String(format: "%7.2f s  TIMER %d", time, t)) }
            if let f = changed.floor {
                print(String(format: "%7.2f s  FLOOR %d of %d%@", time, f.number, f.of, floorSeen == nil ? "" : " — new map"))
                if floorSeen != nil { stitcher.reset(); navigator.forget(); doneTargets = []; lastTimeTarget = nil; settleUntil = time + 2.5 }
                floorSeen = f.number
            }
            let newIntent = hud.objective?.intent(currentArea: hud.area, timed: hud.timedRun) ?? intent
            if newIntent != intent {
                intent = newIntent
                print(String(format: "%7.2f s  INTENT slay %@ travel %@ stay %@ leave %@ to %@ timed %@", time, intent.slay ? "yes" : "no",
                             intent.travel ? "yes" : "no", intent.stayIn ?? "-", intent.leave ?? "-", intent.destination ?? "-", intent.timed ? "yes" : "no"))
            }
        }
    }
    var marks: [CGPoint] = []
    var marker: CGPoint?
    if let chroma = CVPixelBufferGetBaseAddressOfPlane(pixels, 1)?.assumingMemoryBound(to: UInt8.self) {
        marks = RedMarks.find(cbcr: chroma, rowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 1), lumaRect: rect)
        let (outer, inset) = MinimapLayout.outer(rect, windowWidth: CVPixelBufferGetWidthOfPlane(pixels, 0),
                                                 windowHeight: CVPixelBufferGetHeightOfPlane(pixels, 0))
        marker = ObjectiveMarker.find(luma: base, lumaRowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 0),
                                      cbcr: chroma, cbcrRowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 1),
                                      outer: outer, inset: inset)
        if !intent.timed {
            // An ordinary dungeon: the objective marker is the nearest gold ring icon (30 Sep 2026).
            let gold = OrangeIcons.find(luma: base, lumaRowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 0),
                                        cbcr: chroma, cbcrRowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 1),
                                        outer: outer, inset: inset)
            let centre = CGPoint(x: Double(rect.width) / 2, y: Double(rect.height) / 2)
            marker = gold.min { hypot($0.x - centre.x, $0.y - centre.y) < hypot($1.x - centre.x, $1.y - centre.y) }
        }
        // MAPLAB_BLUE=1: where the blue icons are, every 5 s — how often a beacon was near.
        if ProcessInfo.processInfo.environment["MAPLAB_BLUE"] == "1", time - blueSaid >= 5 {
            blueSaid = time
            let blue = BlueIcons.find(luma: base, lumaRowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 0),
                                      cbcr: chroma, cbcrRowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 1),
                                      outer: outer, inset: inset)
            let inside = blue.filter { $0.x > 20 && $0.y > 20 && $0.x < Double(rect.width) - 20 && $0.y < Double(rect.height) - 20 }
            print(String(format: "%7.2f s  BLUE %d icons, %d inside the box%@", time, blue.count, inside.count,
                         inside.map { String(format: " (%.0f,%.0f)", $0.x, $0.y) }.joined()))
        }
        if intent.timed {
            // Beacons only with MAPLAB_BEACONS=1, as the live guide's "Lead to beacons for time"
            // switch (off by default since 28 Sep 2026).
            let beacons: [(CGPoint, String)] = leadToBeacons
                ? BlueIcons.find(luma: base, lumaRowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 0),
                                 cbcr: chroma, cbcrRowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 1),
                                 outer: outer, inset: inset).map { ($0, "beacon") }
                : []
            timeIcons = beacons
                + OrangeIcons.find(luma: base, lumaRowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 0),
                                   cbcr: chroma, cbcrRowBytes: CVPixelBufferGetBytesPerRowOfPlane(pixels, 1),
                                   outer: outer, inset: inset).map { ($0, "afflicted") }
        } else {
            timeIcons = []
        }
    }
    CVPixelBufferUnlockBaseAddress(pixels, .readOnly)

    var reading = MinimapReader.read(gray)
    reading.marks = marks
    // (An ordinary dungeon's marker is the gold objective icon, chosen above.)
    if let m = marker, let arrow = reading.arrow, time - objectiveSaid >= 5 {
        objectiveSaid = time
        print(String(format: "%7.2f s  OBJECTIVE MARKER at (%.0f,%.0f), %@ of the player", time, m.x, m.y, Compass.word(atan2(m.x - arrow.x, -(m.y - arrow.y)))))
    }
    // Time targets (beacons, afflicted packs), as the live guide: inside the box at any time,
    // anywhere when the clock is short, never over a marker on the map; sticky.
    let markerNear = marker.map { $0.x > 40 && $0.y > 40 && $0.x < Double(rect.width) - 40 && $0.y < Double(rect.height) - 40 } == true
    if intent.timed, !markerNear, let arrow = reading.arrow, let player = stitcher.player {
        let left = hud.timer ?? 999
        let inside = timeIcons.filter { $0.0.x > 20 && $0.0.y > 20 && $0.0.x < Double(rect.width) - 20 && $0.0.y < Double(rect.height) - 20 }
        let near: ((CGPoint, String)) -> Double = { hypot($0.0.x - arrow.x, $0.0.y - arrow.y) }
        var candidates = left <= 40 ? timeIcons : inside
        // Reached targets are done; packs count only within 120 px unless the clock is short.
        if let last = lastTimeTarget, hypot(last.x - player.x, last.y - player.y) < 35 { doneTargets.append(last); lastTimeTarget = nil }
        candidates = candidates.filter { c in
            let p = CGPoint(x: player.x + c.0.x - arrow.x, y: player.y + c.0.y - arrow.y)
            if doneTargets.contains(where: { hypot($0.x - p.x, $0.y - p.y) < 120 }) { return false }
            if near(c) < 35 { return false }
            if c.1 != "beacon", left > 40, near(c) > 120 { return false }
            return true
        }
        var chosen: (CGPoint, String)?
        if let last = lastTimeTarget {
            chosen = candidates.filter { hypot(player.x + $0.0.x - arrow.x - last.x, player.y + $0.0.y - arrow.y - last.y) < 50 }.min { near($0) < near($1) }
        }
        if chosen == nil { chosen = candidates.min { near($0) < near($1) } }
        if let b = chosen {
            marker = b.0
            lastTimeTarget = CGPoint(x: player.x + b.0.x - arrow.x, y: player.y + b.0.y - arrow.y)
            if time - beaconSaid >= 5 { beaconSaid = time; print(String(format: "%7.2f s  TIME TARGET %@ at (%.0f,%.0f), %d icons, %d s left", time, b.1, b.0.x, b.0.y, timeIcons.count, left)) }
        } else { lastTimeTarget = nil }
    }
    var markerInBox: CGPoint?
    if let marker, let arrow = reading.arrow {
        markerBearing = atan2(marker.x - arrow.x, -(marker.y - arrow.y))
        markerSeen = time
        if marker.x > 40, marker.y > 40, marker.x < Double(rect.width) - 40, marker.y < Double(rect.height) - 40 { markerInBox = marker }
        if time - markerSaid >= 5 {
            markerSaid = time
            print(String(format: "%7.2f s  MARKER at (%.0f,%.0f) in the box, %@ of the player", time, marker.x, marker.y, Compass.word(markerBearing!)))
        }
    } else if markerBearing != nil, time - markerSeen > 3 {
        markerBearing = nil
    }
    samples += 1
    if time < settleUntil {
        print(String(format: "%7.2f s  settling after the floor change", time))
        continue
    }
    guard reading.isLegible else {
        illegible += 1
        print(String(format: "%7.2f s  illegible: contrast %.1f floor %.0f%% arrow %@ busy %.2f",
                     time, reading.contrast, reading.floorFraction * 100, reading.arrow == nil ? "no" : "yes",
                     reading.busyFraction))
        continue
    }
    var placement = stitcher.add(reading, time: time)
    if placement.placed {
        placed += 1
        refusedSince = nil
    } else if let since = refusedSince {
        if time - since > 2.5 {
            print(String(format: "%7.2f s  fits nowhere for 2.5 s: new map", time))
            stitcher.reset(); navigator.forget()
            refusedSince = nil
            settleUntil = time + 2.5   // usually a fade: the new map begins after it settles
            continue
        }
    } else {
        refusedSince = time
    }
    if spotTimes.count == 2, !spotMarked, time >= spotTimes[0] { spotMarked = stitcher.markSpot() }
    let backToSpot = spotTimes.count == 2 && time >= spotTimes[1]
    var rule = Navigator.AreaRule.none
    if let s = intent.stayIn, let i = stitcher.areaIndex(s) { rule = .stay(i) }
    if let l = intent.leave, let i = stitcher.areaIndex(l) { rule = .leave(i) }
    if let d = intent.destination, let i = stitcher.areaMatching(d), i != stitcher.currentArea {
        rule = .goTo(area: i, from: intent.leave.flatMap { stitcher.areaIndex($0) })
    }
    let guidance = navigator.plan(stitcher, toMarks: (leadToMarks || intent.slay) && (intent.timed || marker == nil), toSpot: backToSpot,
                                  toArches: leadToArches || (intent.travel && !intent.timed), areaRule: rule,
                                  marker: ProcessInfo.processInfo.environment["MAPLAB_NOMARKER"] == "1" ? nil : markerBearing,
                                  markerPoint: markerInBox.flatMap { m in
                                      guard let a = reading.arrow, let p = stitcher.player else { return nil }
                                      return CGPoint(x: p.x + m.x - a.x, y: p.y + m.y - a.y)
                                  }, timed: intent.timed)
    lastGuidance = guidance
    let heading = stitcher.heading().map { Compass.word($0) } ?? "still"
    // FACING <arrow> <movement> in degrees, for checking the arrow reading against travel.
    if let f = reading.facing, let m = stitcher.heading() {
        print(String(format: "%7.2f s  FACING %.0f %.0f", time, f * 180 / .pi, m * 180 / .pi))
    }
    for (kind, point) in placement.placed ? stitcher.newLandmarks : [] {
        let player = stitcher.player ?? point
        print(String(format: "%7.2f s  LANDMARK %@ %@ of the player, %.0f px", time, kind.rawValue,
                     Compass.word(atan2(point.x - player.x, -(point.y - player.y))),
                     hypot(point.x - player.x, point.y - player.y)))
    }
    // The nearest mark's distance from the player, so a stall beside enemies can be told from a wall.
    let nearestMark: String = {
        guard let arrow = reading.arrow, let d = reading.marks.map({ hypot($0.x - arrow.x, $0.y - arrow.y) }).min() else { return "" }
        return String(format: " (nearest %.0f px)", d)
    }()
    let guideText = guidance.map {
        String(format: "go %@ (%.0f°), %.0f px, %d openings, %d marks%@%@", Compass.word($0.bearing), $0.bearing * 180 / .pi,
               $0.distance, $0.openings, $0.marks, nearestMark, $0.toMark ? " TO MARK" : ($0.toSpot ? " TO SPOT" : ($0.toArea ? " TO AREA" : ($0.toWell ? " TO WELL" : ($0.toArch ? " TO ARCH" : ($0.markerInView ? " TO MARKER IN VIEW" : ($0.beeline ? " BEELINE" : ($0.toMarker ? " TO MARKER" : ""))))))))
    } ?? "no opening"
    print(String(format: "%7.2f s  contrast %4.1f  shift %+3d,%+3d score %.2f %@  moving %@  %@",
                 time, reading.contrast, placement.dx, placement.dy, placement.score,
                 placement.placed ? "placed " : "REFUSED", heading, guideText))

    if time >= nextPicture {
        if let image = Picture.overlay(gray, reading) {
            Picture.write(image, to: outURL.appendingPathComponent(String(format: "frame-%03d.png", pictureIndex)))
        }
        if let map = stitcher.picture(path: guidance?.path ?? [], target: guidance?.target) {
            Picture.write(map, to: outURL.appendingPathComponent(String(format: "map-%03d.png", pictureIndex)))
        }
        pictureIndex += 1
        nextPicture = time + pictureEvery
    }
}

if let map = stitcher.picture(path: lastGuidance?.path ?? [], target: lastGuidance?.target) {
    Picture.write(map, to: outURL.appendingPathComponent("map-final.png"))
}
print("\(samples) samples, \(placed) placed, \(illegible) illegible, \(stitcher.recentres) re-centres. Output in \(outURL.path.replacingOccurrences(of: NSHomeDirectory(), with: "~"))")
