import CoreGraphics
import ImageIO
import Foundation

/// Builds one map of the dungeon from minimap readings that scroll under the player.
///
/// Each reading is placed where it best agrees with everything placed so far (not just the
/// previous frame, so small errors do not accumulate), searched over a window of shifts
/// around the last placement. The minimap never rotates or zooms, so a shift is all there is.
/// A reading that agrees poorly everywhere is not placed and does not vote — a wrong vote is
/// worse than a missing one.
///
/// Not thread-safe: one queue owns it.
public final class Stitcher {
    public let size: Int
    /// Per canvas pixel: frames that showed it as floor or wall, as floor, and as wall.
    /// Smooth darkness never votes and votes are never forgotten: a scheme that let fog vote
    /// and forgot old votes (tried 26 Sep) flickered at the edge of the fade and doubled or
    /// trebled the direction swings, however the thresholds were set.
    ///
    /// What keeps a spell's glow off the map instead: **the game reveals nothing new beyond
    /// `revealRadius` of the player.** A pixel never seen before that reads as floor or
    /// wall farther away than that is neither (the glow through the minimap beside the
    /// healing well, 130–300 px from the trail, that filled the fog at the end of the
    /// corridor to the Tomb — as floor first, then as wall once floor was refused). A pixel
    /// already on the map keeps voting at any distance.
    public private(set) var floorVotes: [UInt16]
    public private(set) var seenVotes: [UInt16]
    public private(set) var wallVotes: [UInt16]
    /// Minimap pixels. The fade sits at about 120–140 px in the 26 Sep frames.
    public static let revealRadius = 150.0
    /// Where the current reading's top-left sits on the canvas.
    public private(set) var offset: (x: Int, y: Int)
    /// Every placed arrow position, oldest first, in canvas pixels, with its time.
    public private(set) var trail: [(point: CGPoint, time: Double)] = []
    /// The seen part of the canvas.
    public private(set) var bounds: (minX: Int, minY: Int, maxX: Int, maxY: Int)
    public private(set) var isEmpty = true

    /// Where the player has been, on a grid of `visitCell` pixels: every cell within
    /// `visitRadius` of any trail point. The game reveals the floor round the player as
    /// they walk, so nothing this close to the trail can still be unexplored — dark there
    /// is not floor. Kept here, stamped once per step, because the trail runs to thousands
    /// of points and the navigator plans ten times a second.
    public static let visitCell = 4
    /// 30, not 60: at 60 the mouth of the corridor to the Tomb, 50 px off the trail, could
    /// never be an opening. Ledges beside the trail are sealed by the sharp-edge rule now.
    public static let visitRadius = 30
    public private(set) var visited: [Bool]
    /// Which area each visited cell was walked in: 0 unknown, else index + 1 into
    /// `areaNames`. Stamped with the area name read above the minimap.
    public private(set) var areaAt: [UInt8]
    public private(set) var areaNames: [String] = []
    public private(set) var currentArea: Int?
    /// Where the player crossed from one area to another: the arch used, if one was known
    /// near, else the spot itself. An arch that is a gateway is not "the way in" to
    /// anywhere new, and a gateway out is what "stay in this area" must not route through.
    public private(set) var gateways: [(point: CGPoint, areas: Set<Int>)] = []
    /// Red objective marks, remembered in canvas pixels.
    public private(set) var markMemory = MarkMemory()
    /// A place the owner marked to come back to (an altar, a pedestal) — what blind
    /// players otherwise do by dropping an item there to hear it later.
    public private(set) var spot: CGPoint?
    /// Healing wells and arches seen on this map.
    public private(set) var landmarkMemory = LandmarkMemory()
    /// Landmarks confirmed by the last placed reading — for the guide to announce.
    public private(set) var newLandmarks: [(Landmark, CGPoint)] = []

    /// Marks where the player is now. False when there is no position yet.
    public func markSpot() -> Bool {
        guard let player else { return false }
        spot = player
        return true
    }
    private var lastStamp: CGPoint?

    public static let searchRadius = 28
    public static let minimumScore = 0.35

    public init(size: Int = 3000) {
        self.size = size
        floorVotes = [UInt16](repeating: 0, count: size * size)
        seenVotes = [UInt16](repeating: 0, count: size * size)
        wallVotes = [UInt16](repeating: 0, count: size * size)
        offset = (size / 2, size / 2)
        bounds = (size, size, 0, 0)
        visited = [Bool](repeating: false, count: (size / Self.visitCell) * (size / Self.visitCell))
        areaAt = [UInt8](repeating: 0, count: (size / Self.visitCell) * (size / Self.visitCell))
    }

    public func areaIndex(_ name: String) -> Int? {
        areaNames.firstIndex { $0.caseInsensitiveCompare(name) == .orderedSame }
    }

    /// The known area an objective's destination names ("Siren's Chamber", read with a
    /// letter or two wrong, or as part of a longer phrase), if any.
    public func areaMatching(_ name: String) -> Int? {
        // Never "the destination contains the area's name": with the recogniser failing,
        // "Cave District" was also read as "District", which "District Boss" contains, and
        // the guide led round in circles between the marker and that patch (27 Sep, 07:35).
        let wanted = name.lowercased()
        return areaNames.firstIndex {
            let known = $0.lowercased()
            return known == wanted || known.contains(wanted) || Objective.alike(known, wanted)
        }
    }

    /// The area name above the minimap changed (or was read for the first time).
    public func setArea(_ name: String) {
        let index = areaIndex(name) ?? { areaNames.append(name); return areaNames.count - 1 }()
        guard index != currentArea else { return }
        if let previous = currentArea, let player {
            let arch = landmarkMemory.landmarks
                .filter { $0.kind == .arch && hypot($0.point.x - player.x, $0.point.y - player.y) < 150 }
                .min { hypot($0.point.x - player.x, $0.point.y - player.y) < hypot($1.point.x - player.x, $1.point.y - player.y) }
            gateways.append((arch?.point ?? player, [previous, index]))
        }
        currentArea = index
        // The ground round the player was stamped with the old area on the way in, so the
        // new area's own openings read as outside it and were dropped: 30 s of "no opening"
        // on entering the Ghastly Depths (26 Sep replay). Re-stamp here as the new area.
        if let player { lastStamp = nil; stampVisit(player) }
    }

    public var visitColumns: Int { size / Self.visitCell }

    private func stampVisit(_ p: CGPoint) {
        if let last = lastStamp, hypot(p.x - last.x, p.y - last.y) < Double(Self.visitCell) { return }
        lastStamp = p
        let n = visitColumns, c = Self.visitCell, r = Self.visitRadius / c
        let cx = Int(p.x) / c, cy = Int(p.y) / c
        let stamp = UInt8(min(254, (currentArea ?? -1) + 1))
        for y in max(0, cy - r)...min(n - 1, cy + r) { for x in max(0, cx - r)...min(n - 1, cx + r)
        where (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r {
            visited[y * n + x] = true
            if stamp > 0 { areaAt[y * n + x] = stamp }
        } }
    }

    public func reset() {
        for i in floorVotes.indices { floorVotes[i] = 0; seenVotes[i] = 0; wallVotes[i] = 0 }
        offset = (size / 2, size / 2)
        trail = []
        for i in areaAt.indices { areaAt[i] = 0 }
        gateways = []
        markMemory.clear()
        landmarkMemory.clear()
        newLandmarks = []
        spot = nil
        for i in visited.indices { visited[i] = false }
        lastStamp = nil
        bounds = (size, size, 0, 0)
        isEmpty = true
    }

    public var player: CGPoint? { trail.last?.point }

    /// What a pixel's votes say: 0 unrevealed, 1 floor (two frames and a majority of the
    /// frames that saw it), 2 wall.
    public static func state(seen: Int, floor: Int, wall: Int) -> UInt8 {
        guard seen > 0 else { return 0 }
        return floor * 2 > seen ? (floor >= 2 ? 1 : 0) : 2
    }

    public struct Placement {
        public let dx: Int; public let dy: Int; public let score: Double; public let placed: Bool
    }

    public func add(_ reading: MinimapReading, time: Double) -> Placement {
        guard !isEmpty else {
            vote(reading)
            isEmpty = false
            recordArrow(reading, time: time)
            return Placement(dx: 0, dy: 0, score: 1, placed: true)
        }
        recentreIfNearEdge(reading)
        var best = (score: -2.0, dx: 0, dy: 0)
        let r = Self.searchRadius
        // Coarse pass on a 3-pixel lattice, then a fine pass around the winner.
        for dy in stride(from: -r, through: r, by: 3) {
            for dx in stride(from: -r, through: r, by: 3) {
                let s = agreement(reading, dx: dx, dy: dy, step: 3)
                if s > best.score { best = (s, dx, dy) }
            }
        }
        let coarse = best
        for dy in (coarse.dy - 2)...(coarse.dy + 2) {
            for dx in (coarse.dx - 2)...(coarse.dx + 2) {
                let s = agreement(reading, dx: dx, dy: dy, step: 2)
                if s > best.score { best = (s, dx, dy) }
            }
        }
        guard best.score >= Self.minimumScore else {
            return Placement(dx: best.dx, dy: best.dy, score: best.score, placed: false)
        }
        let nx = offset.x + best.dx, ny = offset.y + best.dy
        guard nx >= 0, ny >= 0, nx + reading.width < size, ny + reading.height < size else {
            return Placement(dx: best.dx, dy: best.dy, score: best.score, placed: false)
        }
        offset = (nx, ny)
        vote(reading)
        recordArrow(reading, time: time)
        return Placement(dx: best.dx, dy: best.dy, score: best.score, placed: true)
    }

    /// Keeps the map away from the canvas's edges by moving everything, so a dungeon larger
    /// than half the canvas in one direction is not mistaken for somewhere new (a placement
    /// off the canvas is refused, and 2.5 s of refusals starts a new map). Rare and cheap
    /// enough: two array copies when the reading comes within `edgeMargin` of an edge.
    static let edgeMargin = 400

    private func recentreIfNearEdge(_ reading: MinimapReading) {
        let m = Self.edgeMargin
        guard offset.x < m || offset.y < m
                || offset.x + reading.width > size - m || offset.y + reading.height > size - m else { return }
        let mapW = bounds.maxX - bounds.minX, mapH = bounds.maxY - bounds.minY
        guard mapW < size - 2 * m, mapH < size - 2 * m else { return }   // nowhere to move it
        let dx = (size - mapW) / 2 - bounds.minX, dy = (size - mapH) / 2 - bounds.minY
        var newFloor = [UInt16](repeating: 0, count: size * size)
        var newSeen = [UInt16](repeating: 0, count: size * size)
        var newWall = [UInt16](repeating: 0, count: size * size)
        for y in bounds.minY...bounds.maxY {
            let from = y * size, to = (y + dy) * size + dx
            for x in bounds.minX...bounds.maxX {
                newFloor[to + x] = floorVotes[from + x]
                newSeen[to + x] = seenVotes[from + x]
                newWall[to + x] = wallVotes[from + x]
            }
        }
        floorVotes = newFloor
        seenVotes = newSeen
        wallVotes = newWall
        offset = (offset.x + dx, offset.y + dy)
        bounds = (bounds.minX + dx, bounds.minY + dy, bounds.maxX + dx, bounds.maxY + dy)
        trail = trail.map { (CGPoint(x: $0.point.x + Double(dx), y: $0.point.y + Double(dy)), $0.time) }
        // Area stamps move by whole cells; the replay below re-stamps with the current area,
        // so keep the old grid and shift it rather than re-stamping.
        let n = visitColumns, cdx = dx / Self.visitCell, cdy = dy / Self.visitCell
        var shifted = [UInt8](repeating: 0, count: areaAt.count)
        for y in 0..<n { for x in 0..<n where areaAt[y * n + x] != 0 {
            let nx = x + cdx, ny = y + cdy
            if nx >= 0, ny >= 0, nx < n, ny < n { shifted[ny * n + nx] = areaAt[y * n + x] }
        } }
        let keepArea = currentArea
        currentArea = nil   // replay stamps nothing
        for i in visited.indices { visited[i] = false }
        lastStamp = nil
        for step in trail { stampVisit(step.point) }
        currentArea = keepArea
        areaAt = shifted
        gateways = gateways.map { (CGPoint(x: $0.point.x + Double(dx), y: $0.point.y + Double(dy)), $0.areas) }
        markMemory.shift(dx: dx, dy: dy)
        if let s = spot { spot = CGPoint(x: s.x + Double(dx), y: s.y + Double(dy)) }
        landmarkMemory.shift(dx: dx, dy: dy)
        recentres += 1
    }

    /// How many times the map has been moved on the canvas — navigation targets held in
    /// canvas coordinates across a move are stale, so holders compare this.
    public private(set) var recentres = 0

    /// Mean agreement (+1 same, −1 different) between the reading and the canvas's majority
    /// at this shift, over pixels both have seen. −2 when there is too little overlap.
    private func agreement(_ reading: MinimapReading, dx: Int, dy: Int, step: Int) -> Double {
        let ox = offset.x + dx, oy = offset.y + dy
        guard ox >= 0, oy >= 0, ox + reading.width < size, oy + reading.height < size else { return -2 }
        var total = 0, count = 0, readable = 0
        var y = 0
        while y < reading.height {
            var x = 0
            let row = (oy + y) * size + ox
            while x < reading.width {
                let i = y * reading.width + x
                if !reading.ignore[i] && withinReach(reading, x: x, y: y) {
                    readable += 1
                    let seen = Int(seenVotes[row + x])
                    if seen > 0 {
                        let canvasFloor = Int(floorVotes[row + x]) * 2 > seen
                        total += (canvasFloor == reading.floor[i]) ? 1 : -1
                        count += 1
                    }
                }
                x += step
            }
            y += step
        }
        // Enough of what this reading can see must already be on the map. Measured against
        // the reading's own readable pixels, not the whole lattice: most dark pixels are
        // unseen now (see MinimapReader), and a fresh dungeon has little floor, so a share
        // of the lattice refused every frame after the first. And only the pixels within
        // `revealRadius` of the player count as readable — the same disc `vote` records —
        // because pixels beyond it can never have been put on the map by this reading's
        // neighbours. Found 29 Sep 2026 (Diablo IV through CrossOver): with the guide turned
        // on mid-floor, the explored patch reached 300 px from the player, the disc held
        // under 40 % of it, every frame after a reset scored −2 and the guide said "New map"
        // every three seconds for as long as it ran.
        guard readable > 0, count * 5 >= readable * 2, count >= 40 else { return -2 }
        return Double(total) / Double(count)
    }

    /// Whether a minimap pixel is within `revealRadius` of the player's arrow — the part of a
    /// reading that `vote` records. Without an arrow, everything counts.
    @inline(__always)
    private func withinReach(_ reading: MinimapReading, x: Int, y: Int) -> Bool {
        guard let arrow = reading.arrow else { return true }
        let dx = Double(x) - arrow.x, dy = Double(y) - arrow.y
        return dx * dx + dy * dy <= Self.revealRadius * Self.revealRadius
    }

    private func vote(_ reading: MinimapReading) {
        for y in 0..<reading.height {
            let row = (offset.y + y) * size + offset.x
            for x in 0..<reading.width {
                let i = y * reading.width + x
                guard !reading.ignore[i] else { continue }
                let k = row + x
                // Nothing new appears beyond the reveal radius: the glow's speckled texture
                // counted as wall once floor was refused, and walled the Tomb off just the
                // same. A pixel never seen before votes only within reach of the player.
                if seenVotes[k] == 0, let arrow = reading.arrow,
                   hypot(Double(x) - arrow.x, Double(y) - arrow.y) > Self.revealRadius { continue }
                guard seenVotes[k] < UInt16.max - 1 else { continue }
                seenVotes[k] += 1
                if reading.floor[i] { floorVotes[k] += 1 } else { wallVotes[k] += 1 }
            }
        }
        bounds = (min(bounds.minX, offset.x), min(bounds.minY, offset.y),
                  max(bounds.maxX, offset.x + reading.width - 1), max(bounds.maxY, offset.y + reading.height - 1))
    }

    private func recordArrow(_ reading: MinimapReading, time: Double) {
        guard let arrow = reading.arrow else { return }
        let point = CGPoint(x: Double(offset.x) + arrow.x, y: Double(offset.y) + arrow.y)
        trail.append((point, time))
        stampVisit(point)
        // Marks go with a placed reading only: on a refused one the offset is unknown.
        let ox = Double(offset.x), oy = Double(offset.y)
        let view = CGRect(x: ox + 10, y: oy + 10, width: Double(reading.width) - 32,
                          height: Double(reading.height) * 0.86 - 10)
        markMemory.update(current: reading.marks.map { CGPoint(x: $0.x + ox, y: $0.y + oy) }, view: view, time: time)
        newLandmarks = landmarkMemory.update(reading.landmarks.map { ($0.0, CGPoint(x: $0.1.x + ox, y: $0.1.y + oy)) })
    }

    /// Direction of travel over the last `window` seconds, radians clockwise from up, if the
    /// player moved far enough for it to mean anything.
    /// The player stood still pushing along `facing` for seconds: whatever is 12–40 px ahead
    /// is not walkable, whatever the minimap says (walkway rails and water read as floor in
    /// the Ziggurat District, 27 Sep). Vote it wall, hard, so the route goes another way.
    public func markBlocked(facing: Double) {
        guard let player else { return }
        for d in stride(from: 12.0, through: 40.0, by: 4) {
            for side in stride(from: -14.0, through: 14.0, by: 4) {
                let x = Int(player.x + sin(facing) * d + cos(facing) * side)
                let y = Int(player.y - cos(facing) * d + sin(facing) * side)
                guard x >= 0, y >= 0, x < size, y < size else { continue }
                let i = y * size + x
                seenVotes[i] = max(seenVotes[i], 40)
                wallVotes[i] = seenVotes[i]
                floorVotes[i] = 0
            }
        }
    }

    /// Which way round an obstacle ahead: the bearing (perpendicular to `facing`) of the side
    /// with more floor 25–60 px out, or nil when neither has any. Spoken as a compass word:
    /// "left" meant the character's left and the owner could not tell whose (27 Sep).
    public func sidestep(facing: Double) -> Double? {
        guard let player else { return nil }
        func floorCount(_ angle: Double) -> Int {
            var n = 0
            for d in stride(from: 25.0, through: 60.0, by: 7) {
                let x = Int(player.x + sin(angle) * d), y = Int(player.y - cos(angle) * d)
                guard x >= 0, y >= 0, x < size, y < size else { continue }
                let i = y * size + x
                if Self.state(seen: Int(seenVotes[i]), floor: Int(floorVotes[i]), wall: Int(wallVotes[i])) == 1 { n += 1 }
            }
            return n
        }
        let left = floorCount(facing - .pi / 2), right = floorCount(facing + .pi / 2)
        if left == 0 && right == 0 { return nil }
        return left >= right ? facing - .pi / 2 : facing + .pi / 2
    }

    public func heading(over window: Double = 0.6, minimumDistance: Double = 8) -> Double? {
        guard let last = trail.last else { return nil }
        guard let earlier = trail.last(where: { last.time - $0.time >= window }) else { return nil }
        let dx = last.point.x - earlier.point.x, dy = last.point.y - earlier.point.y
        guard (dx * dx + dy * dy).squareRoot() >= minimumDistance else { return nil }
        return atan2(dx, -dy)
    }

    /// The seen part of the canvas: floor light, void dark, never-seen black, the trail red,
    /// the last position green, and anything in `marks` (path yellow, target cyan).
    /// `region`: a part of the canvas to draw instead of everything seen. Visited cells are
    /// tinted faintly red, so the visited rule's reach can be seen.
    public func picture(path: [CGPoint] = [], target: CGPoint? = nil, region: CGRect? = nil) -> CGImage? {
        var (minX, minY, maxX, maxY) = bounds
        if let region {
            minX = max(0, Int(region.minX)); minY = max(0, Int(region.minY))
            maxX = min(size - 1, Int(region.maxX)); maxY = min(size - 1, Int(region.maxY))
        }
        guard maxX > minX, maxY > minY else { return nil }
        let w = maxX - minX + 1, h = maxY - minY + 1
        var rgba = [UInt8](repeating: 0, count: w * h * 4)
        for y in 0..<h { for x in 0..<w {
            let i = (minY + y) * size + minX + x
            let seen = Int(seenVotes[i])
            let o = (y * w + x) * 4
            rgba[o + 3] = 255
            guard seen > 0 else { continue }
            // Light for floor, dark grey for wall, near-black for seen-but-unrevealed.
            let state = Stitcher.state(seen: seen, floor: Int(floorVotes[i]), wall: Int(wallVotes[i]))
            let v: UInt8 = state == 1 ? 220 : (state == 2 ? 70 : (state == 3 ? 45 : 25))
            rgba[o] = v; rgba[o + 1] = v; rgba[o + 2] = v
            if region != nil, visited[((minY + y) / Self.visitCell) * visitColumns + (minX + x) / Self.visitCell] {
                rgba[o + 1] = UInt8(Double(v) * 0.75); rgba[o + 2] = UInt8(Double(v) * 0.75)
            }
        } }
        func dot(_ point: CGPoint, _ r: UInt8, _ g: UInt8, _ b: UInt8, radius: Int) {
            let cx = Int(point.x) - minX, cy = Int(point.y) - minY
            for yy in (cy - radius)...(cy + radius) { for xx in (cx - radius)...(cx + radius)
            where xx >= 0 && xx < w && yy >= 0 && yy < h {
                let o = (yy * w + xx) * 4
                rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b
            } }
        }
        for step in trail { dot(step.point, 230, 40, 40, radius: 1) }
        for point in path { dot(point, 240, 220, 40, radius: 1) }
        for mark in markMemory.marks { dot(mark.point, 230, 40, 230, radius: 3) }
        if let spot { dot(spot, 250, 140, 20, radius: 5) }
        for l in landmarkMemory.landmarks {
            if l.kind == .healingWell { dot(l.point, 40, 120, 250, radius: 6) } else { dot(l.point, 250, 250, 60, radius: 6) }
        }
        if let target { dot(target, 40, 220, 240, radius: 5) }
        if let last = trail.last { dot(last.point, 40, 230, 40, radius: 4) }
        return Picture.make(rgba: rgba, width: w, height: h)
    }
}

public enum Picture {
    public static func make(rgba: [UInt8], width: Int, height: Int) -> CGImage? {
        guard let provider = CGDataProvider(data: Data(rgba) as CFData) else { return nil }
        return CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32,
                       bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
                       bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue),
                       provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)
    }

    @discardableResult
    public static func write(_ image: CGImage, to url: URL) -> Bool {
        guard let destination = CGImageDestinationCreateWithURL(url as CFURL, "public.png" as CFString, 1, nil) else { return false }
        CGImageDestinationAddImage(destination, image, nil)
        return CGImageDestinationFinalize(destination)
    }

    /// The minimap as the reader saw it: floor tinted green, ignored pixels blue, arrow red.
    public static func overlay(_ gray: Gray, _ reading: MinimapReading) -> CGImage? {
        let w = gray.width, h = gray.height
        var rgba = [UInt8](repeating: 255, count: w * h * 4)
        for i in 0..<(w * h) {
            let v = Int(gray.pixels[i]) * 3
            let r = min(255, v), g = min(255, v), b = min(255, v)
            rgba[i * 4] = UInt8(r)
            rgba[i * 4 + 1] = UInt8(reading.floor[i] && !reading.ignore[i] ? min(255, g + 90) : g)
            rgba[i * 4 + 2] = UInt8(reading.ignore[i] ? min(255, b + 70) : b)
        }
        if let a = reading.arrow {
            for d in -8...8 {
                for (x, y) in [(Int(a.x) + d, Int(a.y)), (Int(a.x), Int(a.y) + d)] where x >= 0 && x < w && y >= 0 && y < h {
                    let o = (y * w + x) * 4
                    rgba[o] = 255; rgba[o + 1] = 0; rgba[o + 2] = 0
                }
            }
        }
        return make(rgba: rgba, width: w, height: h)
    }
}
