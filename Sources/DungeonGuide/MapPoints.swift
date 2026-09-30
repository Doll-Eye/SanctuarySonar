import AppKit
import ScreenCaptureKit
import Cartography
import Keel

// The world map's points of interest, read off the map screen so a blind player can pick one
// from a list instead of sweeping the map with the cursor (the "zoom and sweep" every guide
// describes; asked for on Blizzard's forums in July 2025, unanswered). Ported from the Windows
// build's MapPoints.cs / Pointer.cs / Shell.cs of 28 Sep 2026, where it was measured.
//
// The map draws its icons as fixed sprites at a constant screen size whatever the zoom, in a
// handful of colours (measured on the 28 Sep 2026 07:12 recording, 2560×1608): waypoints are a
// cyan ring, towns a cyan shield, dungeons a white gate in a black outline (a green leaf on it
// when a Whisper is attached), strongholds a grey badge in a black outline with an orange flame,
// capstones a grey gate with a red padlock, Whisper bounties red skulls and swords, and the
// player a hollow white diamond. So: colour masks, connected blobs, a few size rules. The name of
// a point comes from the game's own tooltip once the pointer is on it (`goToPoint`).

struct MapPoint {
    var kind: String
    var x: Int, y: Int          // picture pixels
    var bearing = 0.0           // from the player, radians, north = up
    var distance = 0.0          // picture pixels from the player
    var name: String?           // from the tooltip, once read
    var text: String { "\(kind) at \(x),\(y)" }
}

enum MapIcons {
    private struct Blob {
        var minX = Int.max, minY = Int.max, maxX = -1, maxY = -1, area = 0
        var w: Int { maxX - minX + 1 }
        var h: Int { maxY - minY + 1 }
        var cx: Int { (minX + maxX) / 2 }
        var cy: Int { (minY + maxY) / 2 }
    }

    /// Every icon found, nearest the player first. `player` is the white diamond if seen,
    /// else the picture's centre (the map opens centred on the player).
    static func find(bgra: [UInt8], width: Int, height: Int) -> (points: [MapPoint], player: CGPoint, diamondSeen: Bool) {
        let s = Double(height) / 1608.0   // icon sizes scale with the picture
        let n = width * height
        var cyan = [UInt8](repeating: 0, count: n), red = cyan, green = cyan, black = cyan, white = cyan, orange = cyan
        bgra.withUnsafeBufferPointer { px in
            var p = 0
            for i in 0..<n {
                let b = Int(px[p]), g = Int(px[p + 1]), r = Int(px[p + 2])
                p += 4
                if g >= 180 && b >= 180 && r <= 170 && g > r + 50 && b > r + 50 { cyan[i] = 1 }
                else if r >= 180 && g <= 100 && b <= 120 { red[i] = 1 }
                else if r >= 200 && g >= 120 && g <= 200 && b <= 80 { orange[i] = 1 }
                else if g >= 190 && g > r + 40 && g > b + 20 && r <= 200 { green[i] = 1 }
                else if r <= 50 && g <= 50 && b <= 50 { black[i] = 1 }
                else if r >= 200 && g >= 200 && b >= 200 { white[i] = 1 }
            }
        }
        func lo(_ v: Double) -> Int { Int((v * s).rounded()) }
        var points: [MapPoint] = []
        var taken: [(x: Int, y: Int)] = []
        func near(_ x: Int, _ y: Int, _ r: Int) -> Bool { taken.contains { abs($0.x - x) <= r && abs($0.y - y) <= r } }
        // The map screen's own furniture, not the map: the tab bar and rank strip across the
        // top, the Whispers bar and buttons across the bottom, the side tabs at the right edge,
        // the event panel bottom-left. Measured at 2560×1608.
        let sx = Double(width) / 2560.0
        func inUI(_ x: Int, _ y: Int) -> Bool {
            Double(y) < 200 * s || Double(y) > Double(height) - 200 * s || Double(x) > Double(width) - 200 * sx
                || (Double(x) < 480 * sx && Double(y) > 1000 * s)
        }
        func count(_ mask: [UInt8], _ b: Blob, _ pad: Int) -> Int {
            var c = 0
            for y in max(0, b.minY - pad)...min(height - 1, b.maxY + pad) {
                for x in max(0, b.minX - pad)...min(width - 1, b.maxX + pad) { c += Int(mask[y * width + x]) }
            }
            return c
        }

        // Black-outlined icons: dungeons, strongholds, capstones, cleared things.
        for b in blobs(black, width, height, minArea: lo(250)) {
            if b.w < lo(30) || b.w > lo(90) || b.h < lo(30) || b.h > lo(90) { continue }
            let kind: String
            if count(orange, b, lo(12)) >= lo(20) { kind = "Stronghold" }
            else if count(red, b, lo(12)) >= lo(20) { kind = "Capstone dungeon" }
            else if count(green, b, lo(14)) >= lo(20) { kind = "Dungeon with a Whisper" }
            else if count(white, b, 0) >= lo(50) { kind = "Dungeon" }
            else { kind = "Marker" }
            points.append(MapPoint(kind: kind, x: b.cx, y: b.cy))
            taken.append((b.cx, b.cy))
        }
        // Cyan: waypoints (a ring) and towns (a larger shield).
        for b in blobs(cyan, width, height, minArea: lo(120)) {
            if b.w < lo(24) || b.w > lo(80) || b.h < lo(24) || b.h > lo(80) { continue }
            if near(b.cx, b.cy, lo(30)) { continue }
            points.append(MapPoint(kind: max(b.w, b.h) <= lo(48) ? "Waypoint" : "Town", x: b.cx, y: b.cy))
            taken.append((b.cx, b.cy))
        }
        // Red on its own: Whisper bounties (the red padlock sits on a black-outlined capstone).
        for b in blobs(red, width, height, minArea: lo(50)) {
            if b.w < lo(14) || b.w > lo(70) || b.h < lo(14) || b.h > lo(70) { continue }
            if near(b.cx, b.cy, lo(34)) { continue }
            points.append(MapPoint(kind: "Whisper", x: b.cx, y: b.cy))
            taken.append((b.cx, b.cy))
        }
        // Green on its own: the green diamond icons (a leaf on a gate belongs to the dungeon).
        for b in blobs(green, width, height, minArea: lo(120)) {
            if b.w < lo(24) || b.w > lo(80) || b.h < lo(24) || b.h > lo(80) { continue }
            if near(b.cx, b.cy, lo(40)) { continue }
            points.append(MapPoint(kind: "Green marker", x: b.cx, y: b.cy))
            taken.append((b.cx, b.cy))
        }

        // Not the screen's furniture, and one point per icon (an outline broken in two gives
        // two blobs a pixel apart).
        var kept: [MapPoint] = []
        for p in points {
            if inUI(p.x, p.y) { continue }
            if kept.contains(where: { abs($0.x - p.x) <= lo(22) && abs($0.y - p.y) <= lo(22) }) { continue }
            kept.append(p)
        }
        points = kept

        // The player: a hollow white diamond about 100 px across, near the middle (the map opens
        // centred on the player; panning moves it, so the nearest such blob to the centre wins).
        // The pointer arrow is smaller and solid, the map's white dots are tiny.
        let centre = CGPoint(x: Double(width) / 2, y: Double(height) / 2)
        var player = centre
        var diamond = false
        var best = Double.infinity
        for b in blobs(white, width, height, minArea: lo(120)) {
            if b.w < lo(40) || b.w > lo(130) || b.h < lo(40) || b.h > lo(130) { continue }
            if inUI(b.cx, b.cy) { continue }
            let fill = Double(b.area) / Double(b.w * b.h)
            if fill > 0.45 { continue }
            let d = abs(Double(b.cx) - centre.x) + abs(Double(b.cy) - centre.y)
            if d > 0.4 * Double(height) || d >= best { continue }
            best = d; player = CGPoint(x: Double(b.cx), y: Double(b.cy)); diamond = true
        }
        for i in points.indices {
            let dx = Double(points[i].x) - player.x, dy = Double(points[i].y) - player.y
            points[i].distance = (dx * dx + dy * dy).squareRoot()
            points[i].bearing = atan2(dx, -dy)
        }
        points.sort { $0.distance < $1.distance }
        return (points, player, diamond)
    }

    /// 8-connected components of a mask with at least `minArea` pixels.
    private static func blobs(_ mask: [UInt8], _ width: Int, _ height: Int, minArea: Int) -> [Blob] {
        var seen = [UInt8](repeating: 0, count: mask.count)
        var result: [Blob] = []
        var stack: [Int] = []
        for start in 0..<mask.count where mask[start] != 0 && seen[start] == 0 {
            var b = Blob()
            seen[start] = 1; stack.append(start)
            while let i = stack.popLast() {
                let x = i % width, y = i / width
                b.area += 1
                if x < b.minX { b.minX = x }; if x > b.maxX { b.maxX = x }
                if y < b.minY { b.minY = y }; if y > b.maxY { b.maxY = y }
                for dy in -1...1 {
                    let yy = y + dy; if yy < 0 || yy >= height { continue }
                    for dx in -1...1 {
                        let xx = x + dx; if xx < 0 || xx >= width { continue }
                        let j = yy * width + xx
                        if mask[j] != 0 && seen[j] == 0 { seen[j] = 1; stack.append(j) }
                    }
                }
            }
            if b.area >= minArea { result.append(b) }
        }
        return result
    }

    /// The picture with every point ringed and the player marked, for checking by eye.
    static func ringed(_ image: CGImage, points: [MapPoint], player: CGPoint) -> CGImage? {
        let w = image.width, h = image.height
        guard let context = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        context.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        context.setLineWidth(4)
        for p in points {
            context.setStrokeColor(p.kind == "Waypoint" ? CGColor(red: 0, green: 1, blue: 1, alpha: 1)
                                   : p.kind.hasPrefix("Dungeon") ? CGColor(red: 1, green: 1, blue: 0, alpha: 1)
                                   : CGColor(red: 1, green: 0, blue: 1, alpha: 1))
            context.strokeEllipse(in: CGRect(x: Double(p.x) - 34, y: Double(h - p.y) - 34, width: 68, height: 68))
        }
        context.setStrokeColor(CGColor(red: 0, green: 1, blue: 0, alpha: 1))
        context.strokeEllipse(in: CGRect(x: player.x - 60, y: Double(h) - player.y - 60, width: 120, height: 120))
        return context.makeImage()
    }

    /// "Waypoint, north-west, near." — what is said for a point.
    static func describe(_ p: MapPoint, width: Int) -> String {
        let unit = Double(width) / 2560.0
        let far = p.distance < 250 * unit ? "near" : p.distance < 650 * unit ? "a little way" : "far"
        return "\(p.name ?? p.kind), \(Compass.word(p.bearing)), \(far)."
    }
}

/// The mouse pointer: the one thing Dungeon Guide sends to the game, and only at the owner's
/// request (28 Sep 2026: "no mouse in vicinity", so the click comes from here too). Moves the
/// pointer to a screen point, and clicks. Nothing else is ever sent. Posting events needs the
/// Accessibility permission; without it the pointer is only warped, which the game may not
/// notice as a hover.
enum Pointer {
    static var isTrusted: Bool { AXIsProcessTrusted() }

    /// Asks once for Accessibility; macOS shows its own prompt the first time.
    @discardableResult
    static func requestTrust() -> Bool {
        if AXIsProcessTrusted() { return true }
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        return AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
    }

    static func move(to point: CGPoint) {
        CGWarpMouseCursorPosition(point)
        CGEvent(mouseEventSource: nil, mouseType: .mouseMoved, mouseCursorPosition: point, mouseButton: .left)?
            .post(tap: .cghidEventTap)
    }

    /// A left click at a screen point.
    static func click(at point: CGPoint) {
        CGEvent(mouseEventSource: nil, mouseType: .leftMouseDown, mouseCursorPosition: point, mouseButton: .left)?
            .post(tap: .cghidEventTap)
        usleep(40_000)
        CGEvent(mouseEventSource: nil, mouseType: .leftMouseUp, mouseCursorPosition: point, mouseButton: .left)?
            .post(tap: .cghidEventTap)
    }

    /// Brings the game to the front so the pointer and the click land on it.
    static func bringToFront(pid: pid_t) -> Bool {
        guard let app = NSRunningApplication(processIdentifier: pid) else { return false }
        if app.isActive { return true }
        return app.activate(options: [.activateIgnoringOtherApps])
    }

    static func isInFront(pid: pid_t) -> Bool { NSWorkspace.shared.frontmostApplication?.processIdentifier == pid }
}

/// The map points as a list to step through (L / K), point at and click (J twice). The only
/// thing that ever goes to the game is the pointer and that click.
@MainActor
final class MapPointsController: ObservableObject {
    @Published private(set) var points: [MapPoint] = []
    @Published private(set) var index = -1
    private var width = 2560
    private var pointedAt = Date.distantPast
    private var pointedIndex = -1
    static let clickWithin: TimeInterval = 15
    private unowned let model: RecorderModel

    init(model: RecorderModel) { self.model = model }

    func describeChosen() -> String {
        guard index >= 0, index < points.count else { return "" }
        return MapIcons.describe(points[index], width: width)
    }

    /// The chosen window and a picture of it, or a spoken reason why not.
    private func capture() async -> (GameWindow, SCWindow, CGImage)? {
        guard let target = await model.captureTarget() else {
            say("No game window chosen. Is the game running?")
            return nil
        }
        let (chosen, window) = target
        do {
            let image = try await Recorder.picture(of: window)
            return (chosen, window, image)
        } catch {
            log("Map points: could not capture \(chosen.label): \(error.localizedDescription)")
            say("Could not capture the game.")
            return nil
        }
    }

    /// Reads the map screen's icons and says what was found and the nearest one.
    func scan() async {
        guard let captured = await capture() else { return }
        let (chosen, _, image) = captured
        let began = CACurrentMediaTime()
        guard let bgra = Self.bgra(of: image) else { say("Could not read the picture."); return }
        let (found, player, diamond) = MapIcons.find(bgra: bgra, width: image.width, height: image.height)
        points = found; width = image.width; index = found.isEmpty ? -1 : 0; pointedIndex = -1
        let ms = Int((CACurrentMediaTime() - began) * 1000)
        log("Map points: \(found.count) in \(ms) ms on \(chosen.label); player \(diamond ? "diamond" : "centre") at \(Int(player.x)),\(Int(player.y)); "
            + found.prefix(40).map(\.text).joined(separator: "; "))
        if found.isEmpty { say("No map points found. Is the map open?"); return }
        var counts: [String: Int] = [:]
        for p in found { counts[p.kind, default: 0] += 1 }
        let kinds = counts.sorted { $0.value > $1.value }.map { "\($0.value) \($0.key)\($0.value == 1 ? "" : "s")" }
        say("\(found.count) points: \(kinds.joined(separator: ", ")). \(sayPoint())")
    }

    private func sayPoint() -> String {
        guard index >= 0 else { return "" }
        return "\(index + 1) of \(points.count). \(MapIcons.describe(points[index], width: width))"
    }

    func next() async {
        if index < 0 { await scan(); return }
        index = (index + 1) % points.count
        say(sayPoint())
    }

    func previous() async {
        if index < 0 { await scan(); return }
        index = (index - 1 + points.count) % points.count
        say(sayPoint())
    }

    /// First press: put the pointer on the chosen point and read the game's tooltip. Second
    /// press on the same point within `clickWithin` seconds: click it.
    func goToPoint() async {
        guard index >= 0, index < points.count else { say("No point chosen. Scan the map first."); return }
        guard let captured = await capture() else { return }
        let (chosen, window, image) = captured
        if !Pointer.isTrusted {
            log("Map points: Accessibility not granted — prompting")
            Pointer.requestTrust()
            say("Allow Dungeon Guide in Accessibility settings to move the pointer, then try again.")
            return
        }
        let p = points[index]
        // Picture pixels to screen points: the window's frame is in screen points, top-left origin.
        let frame = window.frame
        let scaleX = frame.width / Double(image.width), scaleY = frame.height / Double(image.height)
        let screen = CGPoint(x: frame.minX + Double(p.x) * scaleX, y: frame.minY + Double(p.y) * scaleY)
        let pid = window.owningApplication?.processID ?? 0
        if pid != 0, !Pointer.isInFront(pid: pid) {
            let ok = Pointer.bringToFront(pid: pid)
            log("Bring \(chosen.appName) to the front: \(ok)")
            try? await Task.sleep(nanoseconds: 150_000_000)
        }
        if pointedIndex == index, Date().timeIntervalSince(pointedAt) < Self.clickWithin {
            Pointer.move(to: screen)
            try? await Task.sleep(nanoseconds: 80_000_000)
            log("Click: \(p.text) (\(p.name ?? "unnamed")) at screen \(Int(screen.x)),\(Int(screen.y))")
            Pointer.click(at: screen)
            pointedIndex = -1
            say("Clicked.")
            return
        }
        Pointer.move(to: screen)
        log("Pointer on \(p.text) at screen \(Int(screen.x)),\(Int(screen.y))")
        try? await Task.sleep(nanoseconds: 650_000_000)   // the tooltip fades in
        let tip = await readTooltip(window: window, at: p)
        pointedAt = Date(); pointedIndex = index
        if let tip { points[index].name = tip }
        say("\(tip ?? p.kind). Press again to click.")
    }

    /// The game's tooltip for the icon under the pointer: its lines, top to bottom, with the
    /// map's own region names (all capitals) left out. Nil if nothing was read.
    private func readTooltip(window: SCWindow, at p: MapPoint) async -> String? {
        guard let image = try? await Recorder.picture(of: window) else { return nil }
        let s = Double(image.height) / 1608.0
        let rx = max(0, p.x - Int(560 * s)), ry = max(0, p.y - Int(700 * s))
        let rw = min(image.width, p.x + Int(560 * s)) - rx, rh = min(image.height, p.y + Int(120 * s)) - ry
        guard rw > 0, rh > 0, let crop = image.cropping(to: CGRect(x: rx, y: ry, width: rw, height: rh)) else { return nil }
        let lines = TrackerReader.placedLinesXY(in: crop).sorted { $0.y < $1.y }
        log("Tooltip text: " + lines.map(\.text).joined(separator: " | "))
        let kept = lines.map { $0.text.trimmingCharacters(in: .whitespaces) }
            .filter { $0.filter(\.isLetter).count >= 3 }
            .filter { $0.contains(where: \.isLowercase) }   // region names are capitals
            .prefix(4)
        return kept.isEmpty ? nil : kept.joined(separator: ". ")
    }

    /// The picture as BGRA bytes, 4 per pixel, rows packed.
    static func bgra(of image: CGImage) -> [UInt8]? {
        let w = image.width, h = image.height
        var bytes = [UInt8](repeating: 0, count: w * h * 4)
        let info = CGBitmapInfo.byteOrder32Little.rawValue | CGImageAlphaInfo.premultipliedFirst.rawValue
        let ok = bytes.withUnsafeMutableBytes { buffer -> Bool in
            guard let context = CGContext(data: buffer.baseAddress, width: w, height: h, bitsPerComponent: 8,
                                          bytesPerRow: w * 4, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: info) else { return false }
            context.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
            return true
        }
        return ok ? bytes : nil
    }

    private func say(_ text: String) {
        log("Say: \(text)")
        Announcer.say(text)
    }
}
