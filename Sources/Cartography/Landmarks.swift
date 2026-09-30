import CoreGraphics
import Foundation

/// Icons on the minimap worth knowing about: what a sighted player would see appear and
/// what the full map only shows once found. The owner, 25 Sep 2026: "the dungeon map opens
/// empty until I find something like a healing well or barrier" — the minimap shows them
/// the moment they are near, so they are read from it.
///
/// Recognised by the shapes their bright pixels (≥ 100) make, measured on the owner's
/// Forbidden City recordings (446×320 minimap):
/// - **Healing well**: a heart, ~26×20 px, 260–290 bright pixels, with two flat strokes
///   (the bowl's rim, ~23×6 each) just below it. The boss room is usually near one.
/// - **Arch**: a keyhole-shaped arch, two shapes one inside the other, the outer ~22×24.
///   It looks like an area's entrance: on the 22-minute run the objective turned from
///   "Travel to the Ghastly Depths" to "Slay all enemies in the Ghastly Depths" at the very
///   moment one came into view beside the owner (6 min 10 s). One case — so the guide leads
///   to an unvisited arch only while the objective is to travel somewhere.
public enum Landmark: String {
    case healingWell = "Healing well"
    case arch = "Arch"
}

public enum LandmarkReader {
    struct Blob { let x: Int; let y: Int; let w: Int; let h: Int; let area: Int
        var cx: Double { Double(x) + Double(w) / 2 }
        var cy: Double { Double(y) + Double(h) / 2 }
    }

    static let level: UInt8 = 100

    /// Landmarks in minimap pixels. `exclude` is the arrow's position (its shapes are
    /// skipped); nothing in the bottom band (the quest marker) is considered.
    public static func find(_ gray: Gray, bandTop: Int, exclude arrow: CGPoint?) -> [(Landmark, CGPoint)] {
        let w = gray.width
        var seen = [Bool](repeating: false, count: w * gray.height)
        var blobs: [Blob] = []
        for y in 0..<bandTop { for x in 0..<w where gray[x, y] >= level && !seen[y * w + x] {
            var stack = [(x, y)], area = 0, minX = x, maxX = x, minY = y, maxY = y
            seen[y * w + x] = true
            while let (cx, cy) = stack.popLast() {
                area += 1
                minX = min(minX, cx); maxX = max(maxX, cx); minY = min(minY, cy); maxY = max(maxY, cy)
                for (nx, ny) in [(cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1)]
                where nx >= 0 && nx < w && ny >= 0 && ny < bandTop && !seen[ny * w + nx] && gray[nx, ny] >= level {
                    seen[ny * w + nx] = true
                    stack.append((nx, ny))
                }
            }
            guard area >= 25 else { continue }
            let blob = Blob(x: minX, y: minY, w: maxX - minX + 1, h: maxY - minY + 1, area: area)
            if let arrow, hypot(blob.cx - arrow.x, blob.cy - arrow.y) < 25 { continue }
            blobs.append(blob)
        } }

        // The quest marker is a ring (~31×29, thinly filled) with a pin inside, and when its
        // target is in view it sits anywhere on the minimap, not only at the bottom edge —
        // its pin inside its ring read as an arch on 25 Sep. Anything within a ring goes.
        let rings = blobs.filter { $0.w >= 26 && $0.h >= 24 && Double($0.area) / Double($0.w * $0.h) < 0.35 }
        blobs.removeAll { b in
            rings.contains { r in
                b.cx > Double(r.x - 6) && b.cx < Double(r.x + r.w + 6) && b.cy > Double(r.y - 6) && b.cy < Double(r.y + r.h + 6)
            }
        }

        var found: [(Landmark, CGPoint)] = []
        var used = Set<Int>()
        // Healing well: a heart with a flat stroke just below, overlapping it sideways.
        for (i, heart) in blobs.enumerated()
        where (20...32).contains(heart.w) && (14...26).contains(heart.h) && (180...380).contains(heart.area) {
            let rim = blobs.indices.first { j in
                let b = blobs[j]
                return j != i && b.h <= 10 && b.w >= 14 && b.y >= heart.y + heart.h - 2 && b.y <= heart.y + heart.h + 20
                    && b.x < heart.x + heart.w && b.x + b.w > heart.x
            }
            if let rim {
                used.insert(i); used.insert(rim)
                found.append((.healingWell, CGPoint(x: heart.cx, y: heart.cy + 8)))
            }
        }
        // Arch: an outline with a second shape inside it.
        for (i, outer) in blobs.enumerated() where !used.contains(i)
        && (17...28).contains(outer.w) && (19...30).contains(outer.h) {
            let inner = blobs.indices.first { j in
                let b = blobs[j]
                return j != i && !used.contains(j) && b.w < outer.w && b.h <= outer.h
                    && b.cx > Double(outer.x) && b.cx < Double(outer.x + outer.w)
                    && b.cy > Double(outer.y) && b.cy < Double(outer.y + outer.h)
            }
            if let inner {
                used.insert(i); used.insert(inner)
                found.append((.arch, CGPoint(x: outer.cx, y: outer.cy)))
            }
        }
        return found
    }
}

/// Landmarks remembered on the stitched map for as long as the map lasts. Icons do not
/// move, so each is kept once, after `confirmations` sightings in the same place — one odd
/// frame must not invent a healing well. `update` returns the ones confirmed just now.
public struct LandmarkMemory {
    public private(set) var landmarks: [(kind: Landmark, point: CGPoint)] = []
    private var candidates: [(kind: Landmark, point: CGPoint, count: Int)] = []
    /// Within this many pixels, two sightings are one landmark. Wide for wells: the map
    /// drifts over a long run and one well was remembered four times, 90 px apart, on the
    /// 26 Sep Mariner's Refuge run ("Healing well east" said twice in one description).
    static func same(_ kind: Landmark) -> Double { kind == .healingWell ? 120 : 50 }
    static let confirmations = 3

    public init() {}
    public mutating func clear() { landmarks = []; candidates = [] }

    public mutating func update(_ current: [(Landmark, CGPoint)]) -> [(Landmark, CGPoint)] {
        var new: [(Landmark, CGPoint)] = []
        for (kind, point) in current {
            let near: (CGPoint) -> Bool = { hypot($0.x - point.x, $0.y - point.y) < Self.same(kind) }
            if landmarks.contains(where: { $0.kind == kind && near($0.point) }) { continue }
            if let i = candidates.firstIndex(where: { $0.kind == kind && near($0.point) }) {
                candidates[i].count += 1
                if candidates[i].count >= Self.confirmations {
                    landmarks.append((kind, point))
                    new.append((kind, point))
                    candidates.remove(at: i)
                }
            } else {
                candidates.append((kind, point, 1))
            }
        }
        return new
    }

    public mutating func shift(dx: Int, dy: Int) {
        landmarks = landmarks.map { ($0.kind, CGPoint(x: $0.point.x + Double(dx), y: $0.point.y + Double(dy))) }
        candidates = candidates.map { ($0.kind, CGPoint(x: $0.point.x + Double(dx), y: $0.point.y + Double(dy)), $0.count) }
    }
}
