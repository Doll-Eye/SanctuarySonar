import CoreGraphics
import Foundation

/// An 8-bit grey image, row-major.
public struct Gray {
    public let width: Int
    public let height: Int
    public var pixels: [UInt8]

    public init(width: Int, height: Int, pixels: [UInt8]) {
        self.width = width; self.height = height; self.pixels = pixels
    }

    /// Copies a rectangle out of a video-range luma plane (420v plane 0), stretching 16–235
    /// to 0–255 so levels match full-range stills.
    public init(lumaPlane base: UnsafePointer<UInt8>, rowBytes: Int, rect: (x: Int, y: Int, width: Int, height: Int)) {
        width = rect.width; height = rect.height
        var out = [UInt8](repeating: 0, count: rect.width * rect.height)
        for y in 0..<rect.height {
            let row = base + (rect.y + y) * rowBytes + rect.x
            for x in 0..<rect.width {
                out[y * rect.width + x] = UInt8(max(0, min(255, (Int(row[x]) - 16) * 255 / 219)))
            }
        }
        pixels = out
    }

    public subscript(x: Int, y: Int) -> UInt8 { pixels[y * width + x] }

    /// The picture prepared for reading text: everything under `floor` black, the rest
    /// stretched. **Not used**: tried 26 Sep for the tracker over pale stone in Mariner's
    /// Refuge, and it dimmed the objective line (drawn in grey, not white) past reading.
    /// Kept in case a lower floor or a per-line treatment is tried.
    /// Rows above `fromRow` are left as they are: the area name above the minimap is set
    /// smaller and thinner, and the same treatment broke its strokes ("Hollow Ccivems").
    public func textEnhanced(floor: Int = 140, fromRow: Int = 0) -> Gray {
        var out = pixels
        let scale = 255.0 / Double(255 - floor)
        for i in (max(0, fromRow) * width)..<(width * height) {
            let v = Int(pixels[i]) - floor
            out[i] = v <= 0 ? 0 : UInt8(min(255.0, Double(v) * scale))
        }
        return Gray(width: width, height: height, pixels: out)
    }

    public var image: CGImage? {
        guard let provider = CGDataProvider(data: Data(pixels) as CFData) else { return nil }
        return CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 8, bytesPerRow: width,
                       space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGBitmapInfo(rawValue: 0),
                       provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)
    }

    /// Mean over a (2r+1)² box, by integral image. Kills the wall hatching and the floor's
    /// stone texture, which are finer than any corridor.
    public func boxBlur(radius r: Int) -> Gray {
        var integral = [Int](repeating: 0, count: (width + 1) * (height + 1))
        for y in 0..<height {
            var row = 0
            for x in 0..<width {
                row += Int(pixels[y * width + x])
                integral[(y + 1) * (width + 1) + x + 1] = integral[y * (width + 1) + x + 1] + row
            }
        }
        var out = [UInt8](repeating: 0, count: width * height)
        for y in 0..<height {
            let y0 = max(0, y - r), y1 = min(height, y + r + 1)
            for x in 0..<width {
                let x0 = max(0, x - r), x1 = min(width, x + r + 1)
                let sum = integral[y1 * (width + 1) + x1] - integral[y0 * (width + 1) + x1]
                    - integral[y1 * (width + 1) + x0] + integral[y0 * (width + 1) + x0]
                out[y * width + x] = UInt8(sum / ((x1 - x0) * (y1 - y0)))
            }
        }
        return Gray(width: width, height: height, pixels: out)
    }

    /// How busy the texture is round each pixel, in tenths of a grey level: the mean of
    /// |pixel − its 3×3 average| over a 7×7 box. Hatching is busy; smooth darkness and slow
    /// fades are not, because a gradient survives the 3×3 average.
    public func busyness() -> Gray {
        let local = boxBlur(radius: 1)
        var detail = [UInt8](repeating: 0, count: width * height)
        for i in 0..<(width * height) {
            detail[i] = UInt8(min(255, abs(Int(pixels[i]) - Int(local.pixels[i])) * 10))
        }
        return Gray(width: width, height: height, pixels: detail).boxBlur(radius: 3)
    }

    /// Otsu's threshold over the pixels below `ceiling` (the arrow and icons are excluded so
    /// they do not pull the split between floor and void).
    public func otsu(ceiling: UInt8 = 100) -> UInt8 {
        var histogram = [Int](repeating: 0, count: 256)
        for p in pixels where p < ceiling { histogram[Int(p)] += 1 }
        let total = histogram.reduce(0, +)
        guard total > 0 else { return 30 }
        let sumAll = histogram.enumerated().reduce(0) { $0 + $1.offset * $1.element }
        var sumB = 0, weightB = 0, best = 0.0, threshold = 0
        for t in 0..<256 {
            weightB += histogram[t]
            if weightB == 0 { continue }
            let weightF = total - weightB
            if weightF == 0 { break }
            sumB += t * histogram[t]
            let meanB = Double(sumB) / Double(weightB)
            let meanF = Double(sumAll - sumB) / Double(weightF)
            let between = Double(weightB) * Double(weightF) * (meanB - meanF) * (meanB - meanF)
            if between > best { best = between; threshold = t }
        }
        return UInt8(threshold)
    }
}

/// Where the minimap is inside the game picture.
///
/// Measured once, on the 25 Sep 2026 Shadow recording: a 2646×1720 window with a 57-pixel
/// title bar, so a 2646×1663 game picture, minimap at 2166–2611 × 135–455 of the window.
/// Expressed against the picture's height and anchored to its top-right corner, on the
/// assumption that Diablo IV scales its HUD with the picture's height. **Unverified** for
/// other sizes, full screen, and GeForce NOW / PS Remote Play; `MinimapReading.isLegible`
/// is how a wrong guess shows up.
public enum MinimapLayout {
    static let referenceHeight = 1663.0
    static let rightInset = 35.0 / referenceHeight
    static let width = 446.0 / referenceHeight
    static let top = 78.0 / referenceHeight
    static let height = 320.0 / referenceHeight

    /// The minimap's rectangle in window pixels, given the window's pixel size and how much
    /// of its top is title bar rather than game.
    public static func rect(windowWidth: Int, windowHeight: Int, titleBar: Int) -> (x: Int, y: Int, width: Int, height: Int) {
        let pictureHeight = Double(windowHeight - titleBar)
        let w = Int(width * pictureHeight), h = Int(height * pictureHeight)
        let x = windowWidth - Int(rightInset * pictureHeight) - w
        let y = titleBar + Int(top * pictureHeight)
        return (max(0, x), max(0, y), min(w, windowWidth), min(h, windowHeight))
    }

    /// Icons pinned to the minimap's edge — the objective marker in the Undercity, 27 Sep
    /// 2026 — straddle the border, half outside the box, so the capture takes a margin
    /// round it. The reader still sees only the box.
    public static let margin = 30

    /// The box grown by `margin` on every side, clamped to the window, and where the box
    /// sits inside that.
    public static func outer(_ box: (x: Int, y: Int, width: Int, height: Int), windowWidth: Int, windowHeight: Int)
        -> (rect: (x: Int, y: Int, width: Int, height: Int), inset: (x: Int, y: Int)) {
        // Even edges keep the chroma plane (half size) aligned with the luma plane.
        let x = max(0, box.x - margin) & ~1, y = max(0, box.y - margin) & ~1
        let right = min(windowWidth, box.x + box.width + margin), bottom = min(windowHeight, box.y + box.height + margin)
        return ((x, y, right - x, bottom - y), (box.x - x, box.y - y))
    }
}

/// What one frame's minimap says.
public struct MinimapReading {
    /// true where the blurred minimap is floor.
    public var floor: [Bool]
    /// true where the reading should not be trusted: the arrow, icons, the border.
    public var ignore: [Bool]
    /// true where the frame shows smooth darkness: not floor, not wall, just not revealed
    /// yet. Ignored for stitching, but it votes on the map — see `Stitcher.vote`.
    public var unseen: [Bool] = []
    public let width: Int
    public let height: Int
    /// The player's arrow, in minimap pixels, if found.
    public var arrow: CGPoint?
    /// Which way the arrow points — the character's facing — radians clockwise from up.
    public var facing: Double?
    /// The arrow was not found and its position is the box's centre.
    public var arrowAssumed = false
    public let threshold: UInt8
    /// Mean brightness of floor and of void; their gap is how legible this frame is.
    public let floorLevel: Double
    public let voidLevel: Double
    public let floorFraction: Double
    /// Red marks (objective targets) in minimap pixels, found from the chroma plane by the
    /// caller — the reader itself sees only brightness.
    public var marks: [CGPoint] = []
    /// Healing wells and arches in view, in minimap pixels.
    public var landmarks: [(Landmark, CGPoint)] = []

    public var contrast: Double { floorLevel - voidLevel }

    /// Whether this looks like a minimap at all. A menu, the full map, a loading screen or a
    /// wrongly placed rectangle fails at least one of these. From one recording: contrast
    /// ≈ 16 and floor 20–60 % throughout play.
    public var isLegible: Bool {
        arrow != nil && contrast >= 8 && floorFraction > 0.05 && floorFraction < 0.9 && busyFraction < Self.busyLimit
    }

    /// Share of the minimap with busy texture. The minimap measured 0.13–0.17 on 26 Sep, and
    /// up to 0.31 in a tangle of hatched corridors; the inventory screen over it 0.38 —
    /// inventory frames that got past the other tests stitched blocks of junk into the map.
    public var busyFraction: Double = 0
    /// Above this share of busy pixels the frame is a menu (the inventory), not the map.
    /// 0.34 live; a recording's compression adds texture, and the Undercity's fog sat at
    /// 0.35–0.39 on the 27 Sep replay while the live guide read it — MapLab may raise it.
    /// 0.60 since 29 Sep 2026: rendered natively (CrossOver on the Mac, the Shadow PC itself)
    /// the fog texture is far busier than over Shadow's video — Temple District read 0.15–0.33,
    /// the Ziggurat District 0.45–0.48 with the arrow found on every frame, and at 0.34 the
    /// guide dropped 148 of 168 frames ("it kept dropping out"). The other legibility tests
    /// (arrow present, floor share, contrast) still reject menus; the inventory's native
    /// busyness is unmeasured — open it with the guide on and read the Illegible line.
    public nonisolated(unsafe) static var busyLimit = 0.60
}

public enum MinimapReader {
    /// Pixels this bright are the arrow, footprints, icons — never floor or void.
    static let brightLimit: UInt8 = 100
    /// The bottom band is where the edge-clamped quest marker lives. The arrow is never
    /// looked for there, but the band *is* read as map: ignoring all of it (the first
    /// version) made the guide see less far south than any other way, so every southern
    /// corridor looked unexplored until its end — it led the owner into a dead end on
    /// 25 Sep. A disc round the marker is ignored and then filled in from its surroundings.
    static let bottomBand = 0.14
    static let markerRadius = 0.12   // of the minimap's height
    /// The frame's decoration; the right side's is wider and read as map when not excluded.
    static let border = 8
    static let rightBorder = 20
    /// How far to look for readable map when filling in under an icon.
    static let fillReach = 45
    /// Below this busyness (tenths), dark is unseen rather than wall. See `read`.
    static let smoothLimit: UInt8 = 18
    /// A drop in light this big across five pixels of a floor edge makes it a wall.
    static let sharpDrop = 10
    /// Floor patches smaller than this many pixels are specks, not floor.
    static let minimumFloorPatch = 150

    public static func read(_ gray: Gray) -> MinimapReading {
        let w = gray.width, h = gray.height
        let blurred = gray.boxBlur(radius: 4)
        let threshold = max(blurred.otsu(ceiling: brightLimit), 24)

        var ignore = [Bool](repeating: false, count: w * h)
        // Bright things, grown by a few pixels so their halo does not read as floor.
        let grow = 5
        for y in 0..<h { for x in 0..<w where gray[x, y] >= brightLimit {
            for yy in max(0, y - grow)..<min(h, y + grow + 1) {
                for xx in max(0, x - grow)..<min(w, x + grow + 1) { ignore[yy * w + xx] = true }
            }
        } }
        let bandTop = Int(Double(h) * (1 - bottomBand))
        for y in 0..<h { for x in 0..<w
        where x < border || x >= w - rightBorder || y < border || y >= h - border {
            ignore[y * w + x] = true
        } }
        // The quest marker: the bright pixels in the bottom band. Its dark gold parts are
        // not bright, so the whole disc round it goes, not just the bright pixels.
        var mx = 0, my = 0, mcount = 0
        for y in bandTop..<h { for x in 0..<w where gray[x, y] >= brightLimit { mx += x; my += y; mcount += 1 } }
        if mcount >= 20 {
            let cx = mx / mcount, cy = my / mcount, r = Int(markerRadius * Double(h))
            for y in max(0, cy - r)..<min(h, cy + r + 1) { for x in max(0, cx - r)..<min(w, cx + r + 1)
            where (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r {
                ignore[y * w + x] = true
            } }
        }

        var floor = [Bool](repeating: false, count: w * h)
        var floorSum = 0.0, floorCount = 0.0, voidSum = 0.0, voidCount = 0.0
        for i in 0..<(w * h) where !ignore[i] {
            let v = blurred.pixels[i]
            if v > threshold {
                floor[i] = true; floorSum += Double(v); floorCount += 1
            } else {
                voidSum += Double(v); voidCount += 1
            }
        }
        let counted = floorCount + voidCount

        // Fill in under icons from what surrounds them. Left ignored, every icon — the
        // arrow, the quest marker, a healing well, a door — was a patch of map never seen,
        // and a never-seen patch beside floor is an "unexplored opening": on 25 Sep the
        // guide swung between a healing well, a door, and the quest marker's patch (always
        // ~130 px south of the player) for five minutes. Each ignored pixel takes the
        // majority of the first readable pixel in each of the four directions, within
        // `fillReach`. The frame's own border stays ignored: beyond it really is unseen.
        let isBorder: (Int, Int) -> Bool = { x, y in
            x < border || x >= w - rightBorder || y < border || y >= h - border
        }
        var filledFloor = floor, filledIgnore = ignore
        for y in 0..<h { for x in 0..<w where ignore[y * w + x] && !isBorder(x, y) {
            var votes = 0, floors = 0
            for (dx, dy) in [(1, 0), (-1, 0), (0, 1), (0, -1)] {
                var step = 1
                while step <= fillReach {
                    let nx = x + dx * step, ny = y + dy * step
                    if nx < 0 || ny < 0 || nx >= w || ny >= h || isBorder(nx, ny) { break }
                    let j = ny * w + nx
                    if !ignore[j] { votes += 1; if floor[j] { floors += 1 }; break }
                    step += 1
                }
            }
            guard votes >= 2 else { continue }
            filledFloor[y * w + x] = floors * 2 >= votes
            filledIgnore[y * w + x] = false
        } }
        floor = filledFloor
        ignore = filledIgnore

        // Smooth darkness is unseen, not empty. The minimap draws only floor the player has
        // been near: unexplored floor is as dark as solid rock, and the edge of the fog is
        // a soft fade, while a real wall is a light outline with diagonal hatching beyond
        // it. Read as "seen, not floor", every dark pixel closed the explored area in on
        // all sides, and after the owner restarted the dungeon on 25 Sep the guide found no
        // openings for eighteen minutes. So dark pixels count as wall only where the texture
        // is busy (hatching); smooth dark ones are left unseen, and floor that fades into
        // them is an opening. Measured on that run: fade and far darkness 0.3–1.5, hatching
        // 2.2–3.3 (mean absolute difference from a 3×3 average, over 7×7).
        let busy = gray.busyness()
        var busyCount = 0
        for i in 0..<(w * h) where busy.pixels[i] >= smoothLimit { busyCount += 1 }
        var unseen = [Bool](repeating: false, count: w * h)
        for i in 0..<(w * h) where !floor[i] && !ignore[i] && busy.pixels[i] < smoothLimit {
            ignore[i] = true
            unseen[i] = true
        }

        // A sharp edge is a wall even without hatching. Ledges and drops are drawn as a
        // plain edge — the corridor the owner walked on 25 Sep had one along its whole
        // south side, and with smooth dark counted as unseen it read as an endless opening.
        // What sets the fog apart is the fade: 40 → 25 over about twelve pixels, against
        // 40 → 26 within three at a ledge. So where floor meets unseen dark, compare the
        // light a pixel inside the edge with the light four pixels out; a big drop seals
        // the edge with a thin strip of wall, a gentle one leaves it open.
        let fine = gray.boxBlur(radius: 1)
        var sealed: [Int] = []
        for y in 1..<(h - 1) { for x in 1..<(w - 1) where floor[y * w + x] {
            for (dx, dy) in [(1, 0), (-1, 0), (0, 1), (0, -1)] {
                let q = (y + dy) * w + (x + dx)
                guard !floor[q], ignore[q], !isBorder(x + dx, y + dy) else { continue }
                let ix = x - dx, iy = y - dy, ox = x + 4 * dx, oy = y + 4 * dy
                guard ix >= 0, iy >= 0, ix < w, iy < h, ox >= 0, oy >= 0, ox < w, oy < h else { continue }
                let drop = Int(fine[ix, iy]) - Int(fine[ox, oy])
                if drop >= sharpDrop {
                    for step in 1...3 {
                        let sx = x + dx * step, sy = y + dy * step
                        if sx >= 0, sy >= 0, sx < w, sy < h, !floor[sy * w + sx] { sealed.append(sy * w + sx) }
                    }
                }
            }
        } }
        for k in sealed where !isBorder(k % w, k / w) { ignore[k] = false; unseen[k] = false }

        // Specks: patches of "floor" too small to be any real floor — text or effects
        // flashing over the minimap. They became islands of floor in the dark on the
        // 25 Sep map, each one an "opening". Dropped, and left unseen.
        var label = [Int32](repeating: -1, count: w * h)
        for start in 0..<(w * h) where floor[start] && label[start] < 0 {
            var stack = [start], members: [Int] = []
            label[start] = Int32(start)
            while let k = stack.popLast() {
                members.append(k)
                let kx = k % w, ky = k / w
                for (nx, ny) in [(kx + 1, ky), (kx - 1, ky), (kx, ky + 1), (kx, ky - 1)]
                where nx >= 0 && nx < w && ny >= 0 && ny < h {
                    let j = ny * w + nx
                    if floor[j] && label[j] < 0 { label[j] = Int32(start); stack.append(j) }
                }
            }
            if members.count < minimumFloorPatch {
                for k in members { floor[k] = false; ignore[k] = true }
            }
        }

        // The game keeps the player within a few pixels of the box's centre (measured x 209–236,
        // y 147–173 of 446×320). When the arrow is too dim to find — grey in a starting bubble,
        // and on the live capture generally darker than the same frame after the recorder's
        // encode (30 Sep 2026: a nightmare dungeon the replay read on every frame and the live
        // guide on none) — the centre stands in for it, and only the facing is lost.
        let found = findArrow(gray, bandTop: bandTop)
        let arrow: CGPoint? = found ?? (floorCount > 0 ? CGPoint(x: Double(w) / 2, y: Double(h) / 2) : nil)
        var reading = MinimapReading(floor: floor, ignore: ignore, width: w, height: h,
                              arrow: arrow,
                              threshold: threshold,
                              floorLevel: floorCount > 0 ? floorSum / floorCount : 0,
                              voidLevel: voidCount > 0 ? voidSum / voidCount : 0,
                              floorFraction: counted > 0 ? floorCount / counted : 0)
        reading.busyFraction = Double(busyCount) / Double(w * h)
        reading.unseen = unseen
        reading.landmarks = LandmarkReader.find(gray, bandTop: bandTop, exclude: arrow)
        if let found { reading.facing = arrowFacing(gray, at: found) }
        reading.arrowAssumed = found == nil && arrow != nil
        return reading
    }

    /// Measured on the 25 Sep recording: the arrow peaks around 145 after the video-range
    /// stretch, about 220 pixels of it at 100 or more; footprints are a few pixels each.
    static let arrowLevel: UInt8 = 100

    /// The arrow is the bright cluster nearest the middle of the minimap, where the game
    /// keeps the player (measured x 209–236, y 147–173 of 446×320). The first version took
    /// the *largest* bright cluster, and a healing-well icon is larger and brighter than the
    /// arrow: on 25 Sep the player's position kept jumping to the well, every route was
    /// planned from there, and the beacon swung for five minutes.
    static let arrowSearchRadius = 0.25   // of the minimap's height, from its centre

    /// The arrow's facing. The bright pixel farthest from the arrow's centre is its *tail*
    /// (measured: that direction was opposite to travel 77 % of the time on the 22-minute
    /// run), so the facing is from that pixel through the centre.
    public static func arrowFacing(_ gray: Gray, at arrow: CGPoint) -> Double? {
        let r = 18
        var pts: [(Double, Double)] = []
        for y in max(0, Int(arrow.y) - r)..<min(gray.height, Int(arrow.y) + r) {
            for x in max(0, Int(arrow.x) - r)..<min(gray.width, Int(arrow.x) + r) where gray[x, y] >= arrowLevel {
                pts.append((Double(x), Double(y)))
            }
        }
        guard pts.count >= 40 else { return nil }
        let cx = pts.reduce(0) { $0 + $1.0 } / Double(pts.count), cy = pts.reduce(0) { $0 + $1.1 } / Double(pts.count)
        guard let tip = pts.max(by: { hypot($0.0 - cx, $0.1 - cy) < hypot($1.0 - cx, $1.1 - cy) }),
              hypot(tip.0 - cx, tip.1 - cy) >= 5 else { return nil }
        return atan2(cx - tip.0, -(cy - tip.1))
    }

    /// The arrow as the game draws it while the player stands in a floor's starting bubble:
    /// grey, not white (30 Sep 2026, 08:10 — luma peaking at 133 with 15 pixels over 100, against
    /// the 60 the finder wanted; 148 frames "illegible", the objective never read, the guide
    /// silent while the owner waited for it). The second pass takes a dimmer, smaller cluster,
    /// and failing that the box's centre, where the game keeps the player anyway.
    static let dimArrowLevel: UInt8 = 80
    static let dimArrowMinimum = 12
    /// The player is kept within this many pixels of the box's centre; a cluster farther out
    /// than this on the dim pass is an icon, not the arrow.
    static let dimArrowReach = 40.0

    static func findArrow(_ gray: Gray, bandTop: Int) -> CGPoint? {
        if let bright = findArrow(gray, bandTop: bandTop, level: arrowLevel, minimum: 60, reach: arrowSearchRadius * Double(gray.height)) {
            return bright
        }
        return findArrow(gray, bandTop: bandTop, level: dimArrowLevel, minimum: dimArrowMinimum, reach: dimArrowReach)
    }

    private static func findArrow(_ gray: Gray, bandTop: Int, level: UInt8, minimum: Int, reach: Double) -> CGPoint? {
        let w = gray.width
        let centre = (x: Double(w) / 2, y: Double(gray.height) / 2)
        var seen = [Bool](repeating: false, count: w * gray.height)
        var best: (count: Int, sx: Int, sy: Int, gap: Double) = (0, 0, 0, .infinity)
        for y in 0..<bandTop { for x in 0..<w where gray[x, y] >= level && !seen[y * w + x] {
            var stack = [(x, y)], count = 0, sx = 0, sy = 0
            seen[y * w + x] = true
            while let (cx, cy) = stack.popLast() {
                count += 1; sx += cx; sy += cy
                for (nx, ny) in [(cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1)]
                where nx >= 0 && nx < w && ny >= 0 && ny < bandTop && !seen[ny * w + nx] && gray[nx, ny] >= level {
                    seen[ny * w + nx] = true
                    stack.append((nx, ny))
                }
            }
            guard count >= minimum else { continue }
            let gap = hypot(Double(sx) / Double(count) - centre.x, Double(sy) / Double(count) - centre.y)
            if gap < reach && gap < best.gap { best = (count, sx, sy, gap) }
        } }
        guard best.count >= minimum else { return nil }
        return CGPoint(x: Double(best.sx) / Double(best.count), y: Double(best.sy) / Double(best.count))
    }
}
