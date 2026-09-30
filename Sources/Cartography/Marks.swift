import CoreGraphics
import Foundation

/// Red marks on the minimap: the red dots Diablo IV puts on every enemy a "slay all
/// enemies" objective still wants dead, and the red skull on an objective's target.
///
/// Read from the chroma plane (420v plane 1, Cb and Cr interleaved at half resolution).
/// Measured 25 Sep 2026 on the Ghastly Depths: the dots' Cr is 195–200 and Cb about 107,
/// against Cr ≈ 129 for everything else on the minimap; the quest marker's ring never
/// reached Cr 160.
public enum RedMarks {
    static let minimumCr: UInt8 = 160
    static let maximumCb: UInt8 = 122
    /// Chroma pixels in a blob (a dot is ~6 luma pixels across, so ~9 chroma pixels).
    static let minimumBlob = 3

    /// Mark centres in minimap pixels. `lumaRect` is the minimap's rectangle in the luma
    /// plane; the chroma plane is half size in both directions.
    public static func find(cbcr base: UnsafePointer<UInt8>, rowBytes: Int,
                            lumaRect: (x: Int, y: Int, width: Int, height: Int)) -> [CGPoint] {
        let cx0 = lumaRect.x / 2, cy0 = lumaRect.y / 2
        let w = lumaRect.width / 2, h = lumaRect.height / 2
        var red = [Bool](repeating: false, count: w * h)
        for y in 0..<h {
            let row = base + (cy0 + y) * rowBytes + cx0 * 2
            for x in 0..<w {
                let cb = row[x * 2], cr = row[x * 2 + 1]
                red[y * w + x] = cr >= minimumCr && cb <= maximumCb
            }
        }
        var seen = [Bool](repeating: false, count: w * h)
        var marks: [CGPoint] = []
        for start in 0..<(w * h) where red[start] && !seen[start] {
            var stack = [start], count = 0, sx = 0, sy = 0
            seen[start] = true
            while let k = stack.popLast() {
                count += 1; sx += k % w; sy += k / w
                let kx = k % w, ky = k / w
                for (nx, ny) in [(kx + 1, ky), (kx - 1, ky), (kx, ky + 1), (kx, ky - 1)]
                where nx >= 0 && nx < w && ny >= 0 && ny < h {
                    let j = ny * w + nx
                    if red[j] && !seen[j] { seen[j] = true; stack.append(j) }
                }
            }
            guard count >= minimumBlob else { continue }
            // Back to luma (minimap) pixels: chroma pixel centres are at 2x + 0.5.
            marks.append(CGPoint(x: (Double(sx) / Double(count)) * 2 + 1, y: (Double(sy) / Double(count)) * 2 + 1))
        }
        return marks
    }
}

/// The objective marker: the bright cyan icon the game pins to the minimap's edge when the
/// objective is out of view — the Undercity's district boss on 27 Sep 2026, Y ≈ 188,
/// Cb ≈ 136, Cr ≈ 98. Nothing else on the minimap is bright and cyan: the healing well's
/// icon is neutral (Cb 124, Cr 129) and the attunement icons' blue is dim (Y ≈ 108).
public enum ObjectiveMarker {
    static let minimumY: UInt8 = 150
    static let minimumCb: UInt8 = 128
    static let maximumCr: UInt8 = 112
    static let minimumBlob = 6
    /// (A cap on blob size and a "not within 50 px of the player" rule were tried on 27 Sep
    /// against a cyan false marker in the Forbidden City replay: neither removed it, and
    /// together they took the 09:36 Undercity run's floor 2 from 9 / 4 to 15 / 9. Reverted.)

    /// The marker's centre in minimap-box pixels — it may lie outside the box — read from
    /// the captured `outer` rectangle, inside which the box sits at `inset`.
    public static func find(luma: UnsafePointer<UInt8>, lumaRowBytes: Int, cbcr: UnsafePointer<UInt8>, cbcrRowBytes: Int,
                            outer: (x: Int, y: Int, width: Int, height: Int), inset: (x: Int, y: Int)) -> CGPoint? {
        IconFinder.blobs(luma: luma, lumaRowBytes: lumaRowBytes, cbcr: cbcr, cbcrRowBytes: cbcrRowBytes, outer: outer, inset: inset,
                         minimumBlob: minimumBlob) { y, cb, cr in y >= minimumY && cb >= minimumCb && cr <= maximumCr }
            .max { $0.count < $1.count }?.centre
    }
}

/// The Undercity's dim blue icons: braziers to ignite and attunement packs, both of which
/// buy time (measured 27 Sep 2026: Y ≈ 108, Cb ≈ 157, Cr ≈ 104; pinned to the box's edge
/// when out of view, like the marker). The healing well's icon is neutral.
public enum BlueIcons {
    static let maximumY: UInt8 = 150
    static let minimumCb: UInt8 = 150
    static let maximumCr: UInt8 = 115
    static let minimumBlob = 5

    /// Centres in minimap-box pixels, largest first.
    public static func find(luma: UnsafePointer<UInt8>, lumaRowBytes: Int, cbcr: UnsafePointer<UInt8>, cbcrRowBytes: Int,
                            outer: (x: Int, y: Int, width: Int, height: Int), inset: (x: Int, y: Int)) -> [CGPoint] {
        IconFinder.blobs(luma: luma, lumaRowBytes: lumaRowBytes, cbcr: cbcr, cbcrRowBytes: cbcrRowBytes, outer: outer, inset: inset,
                         minimumBlob: minimumBlob) { y, cb, cr in y < maximumY && cb >= minimumCb && cr <= maximumCr }
            .sorted { $0.count > $1.count }.map(\.centre)
    }
}

/// The Undercity's orange icons: afflicted packs, each kill "+5 seconds" (measured 27 Sep
/// 2026 at Y ≈ 110, Cb ≈ 103, Cr ≈ 149 — under the red marks' Cr 160, over the floor's 130).
/// The time bonus of a timed run lives here: 112 → 194 s in four seconds on one pack.
public enum OrangeIcons {
    static let minimumY: UInt8 = 80, maximumY: UInt8 = 210
    static let maximumCb: UInt8 = 115
    static let minimumCr: UInt8 = 140, maximumCr: UInt8 = 159
    static let minimumBlob = 5

    public static func find(luma: UnsafePointer<UInt8>, lumaRowBytes: Int, cbcr: UnsafePointer<UInt8>, cbcrRowBytes: Int,
                            outer: (x: Int, y: Int, width: Int, height: Int), inset: (x: Int, y: Int)) -> [CGPoint] {
        IconFinder.blobs(luma: luma, lumaRowBytes: lumaRowBytes, cbcr: cbcr, cbcrRowBytes: cbcrRowBytes, outer: outer, inset: inset,
                         minimumBlob: minimumBlob) { y, cb, cr in y >= minimumY && y <= maximumY && cb <= maximumCb && cr >= minimumCr && cr <= maximumCr }
            .sorted { $0.count > $1.count }.map(\.centre)
    }
}

enum IconFinder {
    /// Connected blobs of pixels passing `test` (luma, Cb, Cr), with centres in box pixels.
    static func blobs(luma: UnsafePointer<UInt8>, lumaRowBytes: Int, cbcr: UnsafePointer<UInt8>, cbcrRowBytes: Int,
                      outer: (x: Int, y: Int, width: Int, height: Int), inset: (x: Int, y: Int), minimumBlob: Int,
                      test: (UInt8, UInt8, UInt8) -> Bool) -> [(centre: CGPoint, count: Int)] {
        let cx0 = outer.x / 2, cy0 = outer.y / 2, w = outer.width / 2, h = outer.height / 2
        var hit = [Bool](repeating: false, count: w * h)
        for y in 0..<h {
            let crow = cbcr + (cy0 + y) * cbcrRowBytes + cx0 * 2
            let lrow = luma + (outer.y + y * 2) * lumaRowBytes + outer.x
            for x in 0..<w where test(lrow[x * 2], crow[x * 2], crow[x * 2 + 1]) { hit[y * w + x] = true }
        }
        var seen = [Bool](repeating: false, count: w * h)
        var found: [(centre: CGPoint, count: Int)] = []
        for start in 0..<(w * h) where hit[start] && !seen[start] {
            var stack = [start], count = 0, sx = 0, sy = 0
            seen[start] = true
            while let k = stack.popLast() {
                count += 1; sx += k % w; sy += k / w
                let kx = k % w, ky = k / w
                for (nx, ny) in [(kx + 1, ky), (kx - 1, ky), (kx, ky + 1), (kx, ky - 1)]
                where nx >= 0 && nx < w && ny >= 0 && ny < h {
                    let j = ny * w + nx
                    if hit[j] && !seen[j] { seen[j] = true; stack.append(j) }
                }
            }
            if count >= minimumBlob {
                found.append((CGPoint(x: Double(sx) / Double(count) * 2 + 1 - Double(inset.x),
                                      y: Double(sy) / Double(count) * 2 + 1 - Double(inset.y)), count))
            }
        }
        return found
    }
}

/// Red marks remembered on the stitched map, so a target that has gone off the edge of
/// the minimap is still led to. A remembered mark is forgotten when the place it was is
/// in view again with no mark near it — the enemy is dead or has moved — or after
/// `forgetAfter` seconds unseen.
public struct MarkMemory {
    public private(set) var marks: [(point: CGPoint, seen: Double)] = []
    static let merge = 18.0
    /// Enemies move and die: on 26 Sep the guide carried 20–40 remembered marks and led to
    /// positions long empty ("wrong direction" at the end of the run). Half a minute.
    static let forgetAfter = 30.0

    public init() {}

    public mutating func clear() { marks = [] }

    /// `view` is the part of the canvas the reading covered, less its edges and the
    /// quest-marker band, where an absent mark really means absent.
    public mutating func update(current: [CGPoint], view: CGRect, time: Double) {
        marks.removeAll { remembered in
            time - remembered.seen > Self.forgetAfter
                || (view.contains(remembered.point)
                    && !current.contains { hypot($0.x - remembered.point.x, $0.y - remembered.point.y) < Self.merge * 1.5 })
        }
        for point in current {
            if let i = marks.firstIndex(where: { hypot($0.point.x - point.x, $0.point.y - point.y) < Self.merge }) {
                marks[i] = (point, time)
            } else {
                marks.append((point, time))
            }
        }
    }

    public mutating func shift(dx: Int, dy: Int) {
        marks = marks.map { (CGPoint(x: $0.point.x + Double(dx), y: $0.point.y + Double(dy)), $0.seen) }
    }
}
