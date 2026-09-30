import CoreGraphics
import Foundation

/// Where to go next on a stitched map, and which way to push to get there now.
public struct Guidance {
    /// Direction to the next point on the route, radians clockwise from screen-up.
    public let bearing: Double
    /// Length of the whole route to the target, in minimap pixels.
    public let distance: Double
    /// The opening being led to, in canvas pixels.
    public let target: CGPoint
    /// The point a short way along the route that the bearing aims at.
    public let carrot: CGPoint
    /// The route, player first, in canvas pixels.
    public let path: [CGPoint]
    /// How many separate unexplored openings the map has.
    public let openings: Int
    /// The stitcher's `recentres` when this was planned: positions from different values
    /// are in different canvas coordinates and must not be compared.
    public let mapEpoch: Int
    /// The opening led to on the previous plan no longer exists — it was explored and
    /// closed. With a short route before it, that is a dead end.
    public let previousClosed: Bool
    /// Leading to a red objective mark (an enemy to slay, a target), not an opening.
    public let toMark: Bool
    /// How many red marks are remembered on the map.
    public let marks: Int
    /// Leading back to the spot the owner marked.
    public let toSpot: Bool
    /// Leading to an arch not yet walked through — an area's entrance.
    public let toArch: Bool
    /// Leading to unexplored ground beside a healing well, on the way to somewhere: the
    /// blind players' guide says the boss room is usually near one, and on 26 Sep the Tomb
    /// was a plain corridor north of the well, with no icon of its own.
    public let toWell: Bool
    /// Leading to an area already walked through, because the objective is to travel to it.
    public var toArea = false
    /// The opening was chosen with the objective marker's direction weighed in.
    public var toMarker = false
    /// The marker itself is on the map (inside the minimap, not pinned to its edge) and the
    /// route goes straight to it.
    public var markerInView = false
    /// No opening lies towards the marker but the ground that way is not wall: the lead is
    /// the marker's own bearing, straight on, until something stops it.
    public var beeline = false
    /// The other unexplored openings: straight-line bearing from the player and route
    /// length, for saying what else there is besides the one being led to.
    public var others: [(bearing: Double, distance: Double)] = []
}

/// Frontier exploration (Yamauchi 1997) on the stitched minimap.
///
/// The map is reduced to a grid of `cell`-pixel squares: floor, wall, or never seen. An
/// *opening* is a connected run of floor cells next to never-seen cells — the edge of what
/// the minimap has shown, which is where a sighted player heads. The route is Dijkstra over
/// floor, costlier near walls so it keeps to the middle of a corridor, and the bearing aims at
/// a point `carrotDistance` along it rather than at the target: it says which way to go now,
/// around the corner, not in a straight line through the wall.
///
/// It keeps leading to the same opening until that opening is gone or another is much
/// nearer, so the beacon does not swing between two similar choices.
public final class Navigator {
    public static let cell = 4
    /// Openings smaller than this many cells are noise at the edge of a reading.
    static let minimumOpening = 6
    /// Openings nearer than this are already under the player's feet.
    static let minimumDistance = 60.0
    static let carrotDistance = 60.0
    /// A new opening replaces the current one only if its route is this much shorter, or
    /// scores `switchMargin` cells better.
    static let switchRatio = 0.6
    static let switchMargin = 30.0
    /// Pixels within which an opening counts as the one already being led to.
    static let sameOpening = 60.0
    /// Each cell of an opening's width is worth this many cells of walking, up to
    /// `widthCap`: a corridor mouth is a likelier way on than a sliver at the edge of view.
    static let widthBonus = 1.5
    static let widthCap = 25
    /// Cells of walking each cell of distance away from the map's start is worth, capped.
    static let awayBonus = 0.3
    static let awayCap = 60.0
    /// Pixels round a gateway that are walls while staying in its area.
    static let gateBlock = 30.0
    /// Cells added to an opening's score when it is in the area being left.
    static let leavePenalty = 400.0
    /// While travelling, unexplored ground within this many pixels of a healing well is
    /// exempt from the leave penalty and scores this many cells better.
    static let wellRadius = 220.0
    static let wellBonus = 150.0
    /// Enclosed unseen pockets up to this many cells (≈ 40×40 px) are icons, filled as floor.
    static let iconPocket = 100

    private var currentTarget: CGPoint?
    /// Plans in a row on which the opening being led to was not found.
    private var missedPlans = 0
    /// How many plans an opening may be missing before it counts as closed. An opening
    /// at the edge of what has been read flickers — present one frame, gone the next — and
    /// on the 25 Sep run each flicker switched the lead to another opening and back, 66
    /// times in 22 minutes. About a second at the live rate of ten plans a second.
    static let holdPlans = 8
    /// A better opening must stay better for this many plans in a row before the lead moves
    /// to it — openings flicker in during combat (effects over the minimap) and each one
    /// pulled the lead away from a real target and back.
    static let switchPlans = 5
    private var pending: (point: CGPoint, plans: Int)?
    private var currentMark: CGPoint?
    private var markMissedPlans = 0
    /// Set by MapLab to print the candidates of each plan.
    public var debug: ((String) -> Void)?
    private var markSwitchPlans = 0
    private var targetEpoch = 0

    public init() {}

    public func forget() { currentTarget = nil; missedPlans = 0; pending = nil; currentMark = nil; markerHistory = []; seenOpenings = [] }

    /// `toMarks`: the objective is to slay something, so lead to the nearest red mark on
    /// the map when there is one, and to unexplored openings only when there is not.
    /// What the objective says about areas. `stay`: the objective is "… in the <area>" and
    /// the player is in it — offer nothing outside it, and never route back through a
    /// gateway out (the 26 Sep run was led out of the Ghastly Depths a second after its
    /// "slay all enemies" began). `leave`: the objective is to travel to another area —
    /// openings in this one score far worse, and only arches not yet used as gateways count.
    /// `goTo`: the objective names an area already walked through — lead to the nearest
    /// walked ground of it; with none reachable, leave `from` as `leave` would.
    public enum AreaRule: Equatable { case none, stay(Int), leave(Int), goTo(area: Int, from: Int?) }

    /// Cells of walking an opening straight away from the objective marker costs, against
    /// one straight towards it (the cosine in between).
    static let markerWeight = 200.0
    /// With a marker showing, a better opening must beat the current one by this much (in
    /// cells) before the lead moves: the cost-ratio test is dropped, and without a larger
    /// margin the lead flickered between two openings at similar angles (Undercity replay,
    /// 27 Sep: 7 flip-flops in 2.5 min against 0 without the marker).
    static let markerSwitchMargin = 120.0
    /// Beeline: how far ahead must be free of wall, and how far off the marker's bearing an
    /// opening may be and still count as "towards it".
    static let beelineLook = 60.0
    static let beelineAngle = 50.0 * .pi / 180
    /// Off: added on 27 Sep after the good runs and not yet shown to help on its own. The
    /// runs that reached floor 3 (09:16–10:05) had no beeline; measure before turning it on.
    public nonisolated(unsafe) static var beelineEnabled = false
    /// The marker's bearing over the last plans, averaged: the pinned icon jitters.
    private var markerHistory: [Double] = []
    static let markerSmoothing = 10
    /// What the last plan led to: 0 an opening, 1 an area, 2 the marker on the map, 3 a red
    /// mark, 4 the spot. Coming back to openings from anything else is a change of target,
    /// not a dead end (the 07:35 run said "Dead end" at every switch).
    private var lastKind = 0
    /// Openings seen on recent plans, by position. With a marker showing, an opening must
    /// have been there for `settledPlans` plans running before it can be chosen: the fog
    /// edge on the marker's side flickers, and each sliver that appeared won for a second
    /// and vanished (Undercity replay, 27 Sep: 8 flip-flops in 2.5 min; 0 without the marker).
    private var seenOpenings: [(point: CGPoint, plans: Int)] = []
    static let settledPlans = 5

    /// `marker`: the bearing of the objective marker pinned to the minimap's edge, when there
    /// is one — openings towards it score better, so the route heads for the objective
    /// rather than the nearest new ground (the Undercity is timed).
    public func plan(_ map: Stitcher, toMarks: Bool = false, toSpot: Bool = false, toArches: Bool = false,
                     areaRule: AreaRule = .none, marker rawMarker: Double? = nil, markerPoint: CGPoint? = nil,
                     timed: Bool = false) -> Guidance? {
        guard let player = map.player else { return nil }
        var marker: Double?
        if let rawMarker {
            markerHistory.append(rawMarker)
            if markerHistory.count > Self.markerSmoothing { markerHistory.removeFirst() }
            let x = markerHistory.reduce(0.0) { $0 + cos($1) }, y = markerHistory.reduce(0.0) { $0 + sin($1) }
            marker = atan2(y, x)
        } else {
            markerHistory = []
        }
        if map.recentres != targetEpoch { currentTarget = nil; targetEpoch = map.recentres }
        let c = Self.cell
        let (minX, minY, maxX, maxY) = map.bounds
        guard maxX > minX, maxY > minY else { return nil }
        // One cell of never-seen margin all round, so the map's outer edge is an edge.
        let gx0 = minX - c, gy0 = minY - c
        let gw = (maxX - minX) / c + 3, gh = (maxY - minY) / c + 3
        let n = gw * gh

        // 0 unknown, 1 floor, 2 wall — from the centre pixel of each cell.
        var state = [UInt8](repeating: 0, count: n)
        for gy in 0..<gh { for gx in 0..<gw {
            let px = gx0 + gx * c + c / 2, py = gy0 + gy * c + c / 2
            guard px >= 0, py >= 0, px < map.size, py < map.size else { continue }
            let i = py * map.size + px
            let seen = Int(map.seenVotes[i])
            guard seen > 0 else { continue }
            state[gy * gw + gx] = Stitcher.state(seen: seen, floor: Int(map.floorVotes[i]), wall: Int(map.wallVotes[i]))
        } }

        // Holes: never-seen cells that cannot be reached from the map's outer edge without
        // crossing seen ground. Every minimap icon (healing well, door, shrine) is ignored by
        // the reader and so leaves one of these, fixed to the map where the icon sits — and
        // as "never seen" beside floor, each read as an unexplored opening. On 25 Sep the
        // guide swung between a healing well and a door for five minutes. A hole is floor.
        var outside = [Bool](repeating: false, count: n)
        var stack: [Int] = []
        for gx in 0..<gw { stack.append(gx); stack.append((gh - 1) * gw + gx) }
        for gy in 0..<gh { stack.append(gy * gw); stack.append(gy * gw + gw - 1) }
        while let i = stack.popLast() {
            guard !outside[i], state[i] == 0 || state[i] == 3 else { continue }
            outside[i] = true
            let gx = i % gw, gy = i / gw
            if gx > 0 { stack.append(i - 1) }
            if gx < gw - 1 { stack.append(i + 1) }
            if gy > 0 { stack.append(i - gw) }
            if gy < gh - 1 { stack.append(i + gw) }
        }
        // Only icon-sized pockets. Since smooth darkness counts as unseen, the rock between
        // two corridors is an enclosed unseen pocket too, and filling it as floor let a
        // route cut straight through solid rock.
        var pocketSeen = [Bool](repeating: false, count: n)
        for startCell in 0..<n where (state[startCell] == 0 || state[startCell] == 3) && !outside[startCell] && !pocketSeen[startCell] {
            var pocket: [Int] = [], stack = [startCell]
            pocketSeen[startCell] = true
            while let i = stack.popLast() {
                pocket.append(i)
                let gx = i % gw, gy = i / gw
                for j in [gx > 0 ? i - 1 : -1, gx < gw - 1 ? i + 1 : -1, gy > 0 ? i - gw : -1, gy < gh - 1 ? i + gw : -1]
                where j >= 0 && (state[j] == 0 || state[j] == 3) && !outside[j] && !pocketSeen[j] {
                    pocketSeen[j] = true
                    stack.append(j)
                }
            }
            // Icon-sized: floor under an icon. Larger: rock between corridors, which the game
            // never reveals — wall, so it is neither routed through nor an "opening" beside
            // the floor (left unknown, every such pocket made false openings and doubled the
            // direction swings on the 25 Sep run).
            for i in pocket { state[i] = pocket.count <= Self.iconPocket ? 1 : 2 }
        }

        // Staying in an area: the gateways out of it are walls.
        if case .stay(let area) = areaRule {
            // Not the gateway the player is standing in: walling it made the player's own
            // cell a wall with no floor within reach, and the plan returned nothing for the
            // 30 s they stood in the Ghastly Depths' doorway (26 Sep replay).
            for gate in map.gateways where gate.areas.contains(area)
                && hypot(gate.point.x - player.x, gate.point.y - player.y) > Self.gateBlock * 2 {
                let r = Int(Self.gateBlock) / c
                let gx = (Int(gate.point.x) - gx0) / c, gy = (Int(gate.point.y) - gy0) / c
                for y in max(0, gy - r)...min(gh - 1, max(0, gy + r)) { for x in max(0, gx - r)...min(gw - 1, max(0, gx + r))
                where (x - gx) * (x - gx) + (y - gy) * (y - gy) <= r * r {
                    state[y * gw + x] = 2
                } }
            }
        }

        // Distance to the nearest wall, in cells (chamfer, two passes). Never-seen cells are
        // not walls: an opening must not look like a wall to the router.
        let far = 1000
        var wallDistance = [Int](repeating: far, count: n)
        for i in 0..<n where state[i] == 2 { wallDistance[i] = 0 }
        for gy in 0..<gh { for gx in 0..<gw {
            let i = gy * gw + gx
            if gx > 0 { wallDistance[i] = min(wallDistance[i], wallDistance[i - 1] + 1) }
            if gy > 0 { wallDistance[i] = min(wallDistance[i], wallDistance[i - gw] + 1) }
        } }
        for gy in stride(from: gh - 1, through: 0, by: -1) { for gx in stride(from: gw - 1, through: 0, by: -1) {
            let i = gy * gw + gx
            if gx < gw - 1 { wallDistance[i] = min(wallDistance[i], wallDistance[i + 1] + 1) }
            if gy < gh - 1 { wallDistance[i] = min(wallDistance[i], wallDistance[i + gw] + 1) }
        } }

        // The player's cell, snapped to the nearest floor if the arrow sits on a wall pixel.
        var start = cellIndex(player, gx0, gy0, gw, gh)
        if start == nil || state[start!] != 1 {
            start = nearestFloor(to: player, state: state, gx0: gx0, gy0: gy0, gw: gw, gh: gh, within: 6)
        }
        guard let startIndex = start else { return nil }

        // Dijkstra over floor, 8-connected.
        var cost = [Double](repeating: .infinity, count: n)
        var parent = [Int](repeating: -1, count: n)
        var heap = MinHeap()
        cost[startIndex] = 0
        heap.push(0, startIndex)
        let steps: [(Int, Int, Double)] = [(1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1),
                                           (1, 1, 1.414), (1, -1, 1.414), (-1, 1, 1.414), (-1, -1, 1.414)]
        while let (d, i) = heap.pop() {
            if d > cost[i] { continue }
            let gx = i % gw, gy = i / gw
            for (sx, sy, len) in steps {
                let nx = gx + sx, ny = gy + sy
                guard nx >= 0, ny >= 0, nx < gw, ny < gh else { continue }
                let j = ny * gw + nx
                guard state[j] == 1 else { continue }
                // Hugging a wall costs up to three times as much as the corridor's middle.
                let nearWall = 2.0 / Double(max(1, min(wallDistance[j], 4)))
                let next = d + len * (1 + nearWall)
                if next < cost[j] { cost[j] = next; parent[j] = i; heap.push(next, j) }
            }
        }

        func point(_ i: Int) -> CGPoint {
            CGPoint(x: Double(gx0 + (i % gw) * c + c / 2), y: Double(gy0 + (i / gw) * c + c / 2))
        }
        let markCount = map.markMemory.marks.count
        func guidance(to chosen: Int, openings: Int, previousClosed: Bool, toMark: Bool, toSpot: Bool = false,
                      toArch: Bool = false, toWell: Bool = false, toArea: Bool = false,
                      others: [(bearing: Double, distance: Double)] = [], toMarker: Bool = false) -> Guidance {
            // The route, player first.
            var route: [Int] = []
            var k = chosen
            while k >= 0 { route.append(k); k = parent[k] }
            route.reverse()
            let path = route.map(point)
            var walked = 0.0
            var carrot = path.last!
            for idx in 1..<max(path.count, 1) {
                walked += hypot(path[idx].x - path[idx - 1].x, path[idx].y - path[idx - 1].y)
                if walked >= Self.carrotDistance { carrot = path[idx]; break }
            }
            let bearing = atan2(carrot.x - player.x, -(carrot.y - player.y))
            let length = zip(path, path.dropFirst()).reduce(0.0) { $0 + hypot($1.1.x - $1.0.x, $1.1.y - $1.0.y) }
            return Guidance(bearing: bearing, distance: length, target: point(chosen), carrot: carrot,
                            path: path, openings: openings, mapEpoch: map.recentres,
                            previousClosed: previousClosed, toMark: toMark, marks: markCount, toSpot: toSpot,
                            toArch: toArch, toWell: toWell, toArea: toArea, toMarker: toMarker, others: others)
        }

        // Back to the marked spot, when asked: before marks and openings. If no route is
        // known yet it falls through, and the guide says so.
        if toSpot, let spot = map.spot {
            var cellAt = cellIndex(spot, gx0, gy0, gw, gh)
            if cellAt == nil || state[cellAt!] != 1 || !cost[cellAt!].isFinite {
                cellAt = nearestFloor(to: spot, state: state, gx0: gx0, gy0: gy0, gw: gw, gh: gh, within: 8)
            }
            if let cellAt, cost[cellAt].isFinite {
                lastKind = 4
                return guidance(to: cellAt, openings: 0, previousClosed: false, toMark: false, toSpot: true)
            }
        }

        // A red mark wins over any opening while the objective is to slay: route to the
        // floor cell nearest the reachable mark with the shortest route. A mark sitting on a
        // wall pixel or in a gap of the map snaps to floor within six cells.
        if toMarks {
            var bestMark: Int?
            var reachable: [(cell: Int, point: CGPoint)] = []
            for mark in map.markMemory.marks {
                // A mark beyond the fog's edge is routed to the nearest reachable floor within
                // 60 px, not dropped: dropped, it blinked the lead between the mark and an
                // opening the other way, frame by frame (Mariner's Refuge, 26 Sep).
                var cellAt = cellIndex(mark.point, gx0, gy0, gw, gh)
                if cellAt == nil || state[cellAt!] != 1 || !cost[cellAt!].isFinite {
                    cellAt = nearestReachableFloor(to: mark.point, state: state, cost: cost, gx0: gx0, gy0: gy0, gw: gw, gh: gh, within: 15)
                }
                guard let cellAt, cost[cellAt].isFinite, cost[cellAt] * Double(c) >= 12 else { continue }
                reachable.append((cellAt, mark.point))
                if bestMark == nil || cost[cellAt] < cost[bestMark!] { bestMark = cellAt }
            }
            // Stay with the mark being led to while it exists, unless another is decisively
            // nearer for a while: enemies move, and the nearest one changed several times a
            // minute in combat on 26 Sep (102 of 148 direction swings were in mark-leading).
            // A mark that has just gone (or lost its route) is held for `holdPlans`, like an
            // opening: enemies' marks blink, and every blink was a swing of the beacon.
            if bestMark == nil, markMissedPlans < Self.holdPlans, let held = currentMark,
               let cell = nearestReachableFloor(to: held, state: state, cost: cost, gx0: gx0, gy0: gy0, gw: gw, gh: gh, within: 15) {
                markMissedPlans += 1
                lastKind = 3
                return guidance(to: cell, openings: 0, previousClosed: false, toMark: true)
            }
            if let bestMark {
                markMissedPlans = 0
                var chosenMark = bestMark
                if let held = currentMark,
                   let same = reachable.min(by: { hypot($0.point.x - held.x, $0.point.y - held.y) < hypot($1.point.x - held.x, $1.point.y - held.y) }),
                   hypot(same.point.x - held.x, same.point.y - held.y) < 40 {
                    let decisive = cost[bestMark] <= Self.switchRatio * cost[same.cell]
                    markSwitchPlans = decisive ? markSwitchPlans + 1 : 0
                    if !decisive || markSwitchPlans < Self.switchPlans { chosenMark = same.cell }
                }
                currentMark = reachable.first { $0.cell == chosenMark }?.point
                lastKind = 3
                return guidance(to: chosenMark, openings: 0, previousClosed: false, toMark: true)
            }
            currentMark = nil
            markMissedPlans = 0
        }

        // While the objective is to travel somewhere: an arch the player has not walked
        // through — nothing within the visited radius of the trail — before any opening.
        // Travelling: arches not yet used join the openings as candidates, weighed by route
        // like any other. They used to win outright, and on 26 Sep that led the owner 800 px
        // to the wrong arch for four minutes while the Tomb was a corridor beside the well.
        var archCells = Set<Int>()
        if toArches {
            for l in map.landmarkMemory.landmarks where l.kind == .arch {
                // An arch already crossed from one area to another is not the way to a new one.
                if map.gateways.contains(where: { hypot($0.point.x - l.point.x, $0.point.y - l.point.y) < 40 }) { continue }
                // Leaving an area, an arch walked past still counts — the Tomb's door may have
                // been passed on the way in. Otherwise one beside the trail was walked through.
                let vx = Int(l.point.x) / Stitcher.visitCell, vy = Int(l.point.y) / Stitcher.visitCell
                if case .leave = areaRule {} else if vx >= 0, vy >= 0, vx < map.visitColumns, vy < map.visitColumns,
                   map.visited[vy * map.visitColumns + vx] { continue }
                var cellAt = cellIndex(l.point, gx0, gy0, gw, gh)
                if cellAt == nil || state[cellAt!] != 1 || !cost[cellAt!].isFinite {
                    cellAt = nearestFloor(to: l.point, state: state, gx0: gx0, gy0: gy0, gw: gw, gh: gh, within: 8)
                }
                guard let cellAt, cost[cellAt].isFinite, cost[cellAt] * Double(c) >= Self.minimumDistance else { continue }
                archCells.insert(cellAt)
            }
        }
        let wells = map.landmarkMemory.landmarks.filter { $0.kind == .healingWell }.map(\.point)
        func nearWell(_ p: CGPoint) -> Bool {
            toArches && wells.contains { hypot($0.x - p.x, $0.y - p.y) <= Self.wellRadius }
        }

        // Openings: floor cells beside never-seen cells, grouped by connectivity.
        var isFrontier = [Bool](repeating: false, count: n)
        for gy in 1..<(gh - 1) { for gx in 1..<(gw - 1) {
            let i = gy * gw + gx
            guard state[i] == 1, cost[i].isFinite else { continue }
            // Beside the trail nothing is unexplored: the game revealed it as the player
            // passed. A ledge the owner walked along read as an opening until this.
            let px = gx0 + gx * c + c / 2, py = gy0 + gy * c + c / 2
            if px >= 0, py >= 0, px < map.size, py < map.size,
               map.visited[(py / Stitcher.visitCell) * map.visitColumns + px / Stitcher.visitCell] { continue }
            // Beside fog, or beside the flickering edge of it (state 3): demanding pure fog
            // left almost no openings, since the edge rings every fade.
            let fog: (Int) -> Bool = { state[$0] == 0 || state[$0] == 3 }
            if fog(i - 1) || fog(i + 1) || fog(i - gw) || fog(i + gw) {
                isFrontier[i] = true
            }
        } }
        var group = [Int](repeating: -1, count: n)
        var openings: [(nearest: Int, size: Int, cells: [Int])] = []
        for i in 0..<n where isFrontier[i] && group[i] < 0 {
            var stack = [i], members = 0, nearest = i, cells: [Int] = []
            group[i] = openings.count
            while let k = stack.popLast() {
                members += 1
                cells.append(k)
                if cost[k] < cost[nearest] { nearest = k }
                let kx = k % gw, ky = k / gw
                for (sx, sy, _) in steps {
                    let nx = kx + sx, ny = ky + sy
                    guard nx >= 0, ny >= 0, nx < gw, ny < gh else { continue }
                    let j = ny * gw + nx
                    if isFrontier[j] && group[j] < 0 { group[j] = openings.count; stack.append(j) }
                }
            }
            openings.append((nearest, members, cells))
        }
        let pixelsPerCost = Double(c)
        var usable = openings.filter {
            $0.size >= Self.minimumOpening && cost[$0.nearest] * pixelsPerCost >= Self.minimumDistance
        }
        for cell in archCells where !usable.contains(where: { $0.cells.contains(cell) }) {
            usable.append((cell, Self.widthCap, [cell]))
        }
        if case .stay(let area) = areaRule {
            usable.removeAll { o in
                let p = CGPoint(x: Double(gx0 + (o.nearest % gw) * c + c / 2), y: Double(gy0 + (o.nearest / gw) * c + c / 2))
                if let a = areaOfOpening(p, map), a != area { return true }
                return false
            }
        }
        // The objective marker inside the minimap (the boss room in view, 27 Sep, third
        // Undercity run, with 26 s left): straight to the nearest floor with a route to it.
        // Only when there is reachable floor right by it (4 cells): a marker across water or a
        // wall had "reachable floor" 80 px away on the near side, and the route ran at the
        // wall (27 Sep, Ziggurat District). Otherwise the openings are weighed by how near
        // they are to the marker's point, below.
        if let markerPoint,
           let cell = nearestReachableFloor(to: markerPoint, state: state, cost: cost, gx0: gx0, gy0: gy0, gw: gw, gh: gh, within: 20) {
            lastKind = 2
            currentTarget = point(cell)
            let others = usable.map { o -> (bearing: Double, distance: Double) in
                let p = point(o.nearest)
                return (atan2(p.x - player.x, -(p.y - player.y)), cost[o.nearest] * pixelsPerCost)
            }
            var g = guidance(to: cell, openings: usable.count, previousClosed: false, toMark: false, others: others, toMarker: true)
            g.markerInView = true
            return g
        }
        // Travelling to an area already walked through: the nearest ground stamped with it
        // that a route reaches — the border crossed on the way out, in practice.
        var rule = areaRule
        if case .goTo(let wanted, let from) = rule {
            let n = map.visitColumns, vc = Stitcher.visitCell
            var best: Int?
            for vy in max(0, minY / vc)...min(n - 1, maxY / vc) { for vx in max(0, minX / vc)...min(n - 1, maxX / vc)
            where map.areaAt[vy * n + vx] == UInt8(wanted + 1) {
                let p = CGPoint(x: Double(vx * vc + vc / 2), y: Double(vy * vc + vc / 2))
                guard let i = cellIndex(p, gx0, gy0, gw, gh), state[i] == 1, cost[i].isFinite,
                      cost[i] * Double(c) >= Self.minimumDistance else { continue }
                if best == nil || cost[i] < cost[best!] { best = i }
            } }
            if let best {
                lastKind = 1
                currentTarget = point(best)
                let others = usable.map { o -> (bearing: Double, distance: Double) in
                    let p = point(o.nearest)
                    return (atan2(p.x - player.x, -(p.y - player.y)), cost[o.nearest] * pixelsPerCost)
                }
                return guidance(to: best, openings: usable.count, previousClosed: false, toMark: false, toArea: true, others: others)
            }
            rule = from.map { .leave($0) } ?? .none
        }
        // How long each opening has been about, by position; under a marker, only settled
        // ones (and the one already being led to) may be chosen.
        var stillSeen: [(point: CGPoint, plans: Int)] = []
        var settled = [Bool](repeating: false, count: usable.count)
        for (k, o) in usable.enumerated() {
            let p = point(o.nearest)
            let plans = (seenOpenings.first { hypot($0.point.x - p.x, $0.point.y - p.y) < Self.sameOpening }?.plans ?? 0) + 1
            stillSeen.append((p, plans))
            settled[k] = plans >= Self.settledPlans
        }
        seenOpenings = stillSeen
        if marker != nil {
            let keep = usable.indices.filter { k in
                settled[k] || currentTarget.map { t in usable[k].cells.contains { hypot(point($0).x - t.x, point($0).y - t.y) < Self.sameOpening } } == true
            }
            if !keep.isEmpty { usable = keep.map { usable[$0] } }
        }
        if lastKind != 0 { currentTarget = nil; missedPlans = 0; pending = nil; lastKind = 0 }
        // Straight at the marker when nothing else serves. Standing at the fog's edge pushing
        // towards it, the new ground is within the walked radius and is no opening, so the
        // nearest opening was behind or beside the player (the 10:33 run: marker west, lead
        // north for a minute). A sighted player walks at the marker until a wall stops them;
        // so does this, if the next `beelineLook` px that way hold no wall.
        func beeline(_ bearing: Double) -> Guidance? {
            var last = player
            for d in stride(from: 8.0, through: Self.beelineLook, by: 8) {
                let p = CGPoint(x: player.x + sin(bearing) * d, y: player.y - cos(bearing) * d)
                guard let i = cellIndex(p, gx0, gy0, gw, gh), state[i] != 2 else { return nil }
                last = p
            }
            currentTarget = nil
            var g = Guidance(bearing: bearing, distance: Self.beelineLook, target: last, carrot: last, path: [player, last],
                             openings: usable.count, mapEpoch: map.recentres, previousClosed: false, toMark: false,
                             marks: markCount, toSpot: false, toArch: false, toWell: false)
            g.toMarker = true
            g.beeline = true
            g.others = usable.map { o in
                let p = point(o.nearest)
                return (atan2(p.x - player.x, -(p.y - player.y)), cost[o.nearest] * pixelsPerCost)
            }
            return g
        }
        if Self.beelineEnabled, let marker, !usable.contains(where: { o in
            let p = point(o.nearest)
            var d = abs(atan2(p.x - player.x, -(p.y - player.y)) - marker).truncatingRemainder(dividingBy: 2 * .pi)
            if d > .pi { d = 2 * .pi - d }
            return d < Self.beelineAngle
        }), let g = beeline(marker) {
            lastKind = 5
            return g
        }
        let hadTarget = currentTarget != nil
        guard !usable.isEmpty else { currentTarget = nil; return nil }

        // Dungeons follow a formula (TJ the Blind Gamer's guide): you start in one part of
        // the map and the objectives and the boss are in others. So an opening farther from
        // where this map began than the player is now scores a little better — gently, so
        // a much nearer opening still wins.
        let origin = map.trail.first?.point ?? player
        let playerReach = hypot(player.x - origin.x, player.y - origin.y)
        func score(_ o: (nearest: Int, size: Int, cells: [Int])) -> Double {
            let p = point(o.nearest)
            let onward = (hypot(p.x - origin.x, p.y - origin.y) - playerReach) / Double(c)
            var s = cost[o.nearest] - Self.widthBonus * Double(min(o.size, Self.widthCap))
                - Self.awayBonus * max(-Self.awayCap, min(Self.awayCap, onward))
            if nearWell(p) && !timed {
                s -= Self.wellBonus
            } else if case .leave(let area) = rule, areaOfOpening(p, map) == area {
                s += Self.leavePenalty
            }
            if let marker {
                let towards = atan2(p.x - player.x, -(p.y - player.y))
                s += Self.markerWeight * (1 - cos(towards - marker))
            }
            return s
        }
        if let debug {
            let list = usable.map { o -> String in
                let p = point(o.nearest)
                return String(format: "(%.0f,%.0f) cost %.0f size %d well %@ area %@ arch %@ score %.0f", p.x, p.y, cost[o.nearest], o.size,
                              nearWell(p) ? "Y" : "n", areaOfOpening(p, map).map(String.init) ?? "-", archCells.contains(o.nearest) ? "Y" : "n", score(o))
            }
            debug("player (\(Int(player.x)),\(Int(player.y))) wells \(wells.map { "(\(Int($0.x)),\(Int($0.y)))" }) rule \(rule) candidates: " + list.joined(separator: " | "))
        }
        let best = usable.min { score($0) < score($1) }!
        var chosen = best.nearest
        var previousClosed = false
        if let previous = currentTarget {
            // The same opening is whichever one has any cell near where the last target was:
            // an opening's nearest cell slides along it as the player moves, so comparing
            // only nearest cells (the first version) lost it every few steps and swung the
            // beacon between two openings.
            func gap(_ o: (nearest: Int, size: Int, cells: [Int])) -> Double {
                o.cells.lazy.map { hypot(point($0).x - previous.x, point($0).y - previous.y) }.min() ?? .infinity
            }
            // The nearest, not the first within reach: where openings sit shoulder to shoulder
            // round a small explored patch (the Undercity, 27 Sep), "first within 60 px" was a
            // neighbour of the target, and the target walked along the chain plan by plan.
            if let same = usable.min(by: { gap($0) < gap($1) }), gap(same) < Self.sameOpening {
                missedPlans = 0
                // Stay unless the best is decisively nearer, and has been for a while.
                let decisive = marker != nil
                    ? score(best) <= score(same) - Self.markerSwitchMargin
                    : score(best) <= score(same) - Self.switchMargin && cost[best.nearest] <= Self.switchRatio * cost[same.nearest]
                if decisive {
                    let p = point(best.nearest)
                    if let waiting = pending, hypot(waiting.point.x - p.x, waiting.point.y - p.y) < Self.sameOpening {
                        pending = (p, waiting.plans + 1)
                    } else {
                        pending = (p, 1)
                    }
                }
                if !decisive || (pending?.plans ?? 0) < Self.switchPlans {
                    chosen = same.nearest
                    if !decisive { pending = nil }
                } else {
                    pending = nil
                }
            } else if missedPlans < Self.holdPlans,
                      let held = cellIndex(previous, gx0, gy0, gw, gh)
                        .flatMap({ state[$0] == 1 && cost[$0].isFinite ? $0 : nil })
                        ?? nearestFloor(to: previous, state: state, gx0: gx0, gy0: gy0, gw: gw, gh: gh, within: 5)
                        .flatMap({ cost[$0].isFinite ? $0 : nil }) {
                // Missing, but not for long: keep leading to where it was.
                missedPlans += 1
                chosen = held
            } else {
                missedPlans = 0
                previousClosed = true
            }
        }
        if let debug {
            let c = point(chosen), b = point(best.nearest)
            debug(String(format: "CHOICE (%.0f,%.0f) best (%.0f,%.0f) previous %@ pending %d missed %d closed %@ usable %d",
                         c.x, c.y, b.x, b.y, currentTarget.map { "(\(Int($0.x)),\(Int($0.y)))" } ?? "-",
                         pending?.plans ?? 0, missedPlans, previousClosed ? "Y" : "n", usable.count))
        }
        currentTarget = point(chosen)
        let others = usable.filter { $0.nearest != chosen }.map { o -> (bearing: Double, distance: Double) in
            let p = point(o.nearest)
            return (atan2(p.x - player.x, -(p.y - player.y)), cost[o.nearest] * pixelsPerCost)
        }
        return guidance(to: chosen, openings: usable.count, previousClosed: previousClosed && hadTarget, toMark: false,
                        toArch: archCells.contains(chosen), toWell: nearWell(point(chosen)) && !timed, others: others,
                        toMarker: marker != nil)
    }

    /// The area a point belongs to: the stamp of the nearest walked cell, within 160 px.
    private func areaOfOpening(_ p: CGPoint, _ map: Stitcher) -> Int? {
        let n = map.visitColumns, vx = Int(p.x) / Stitcher.visitCell, vy = Int(p.y) / Stitcher.visitCell
        for r in 0...40 {
            for y in max(0, vy - r)...min(n - 1, vy + r) { for x in max(0, vx - r)...min(n - 1, vx + r)
            where abs(x - vx) == r || abs(y - vy) == r {
                let a = map.areaAt[y * n + x]
                if a > 0 { return Int(a) - 1 }
            } }
        }
        return nil
    }

    private func cellIndex(_ p: CGPoint, _ gx0: Int, _ gy0: Int, _ gw: Int, _ gh: Int) -> Int? {
        let gx = (Int(p.x) - gx0) / Self.cell, gy = (Int(p.y) - gy0) / Self.cell
        guard gx >= 0, gy >= 0, gx < gw, gy < gh else { return nil }
        return gy * gw + gx
    }

    /// The nearest floor cell with a route from the player, within `within` cells.
    private func nearestReachableFloor(to p: CGPoint, state: [UInt8], cost: [Double], gx0: Int, gy0: Int, gw: Int, gh: Int, within: Int) -> Int? {
        let cx = (Int(p.x) - gx0) / Self.cell, cy = (Int(p.y) - gy0) / Self.cell
        var best: (Int, Int)? = nil
        for dy in -within...within { for dx in -within...within {
            let x = cx + dx, y = cy + dy
            guard x >= 0, y >= 0, x < gw, y < gh, state[y * gw + x] == 1, cost[y * gw + x].isFinite else { continue }
            let d = dx * dx + dy * dy
            if best == nil || d < best!.1 { best = (y * gw + x, d) }
        } }
        return best?.0
    }

    private func nearestFloor(to p: CGPoint, state: [UInt8], gx0: Int, gy0: Int, gw: Int, gh: Int, within: Int) -> Int? {
        let cx = (Int(p.x) - gx0) / Self.cell, cy = (Int(p.y) - gy0) / Self.cell
        var best: (Int, Int)? = nil
        for dy in -within...within { for dx in -within...within {
            let x = cx + dx, y = cy + dy
            guard x >= 0, y >= 0, x < gw, y < gh, state[y * gw + x] == 1 else { continue }
            let d = dx * dx + dy * dy
            if best == nil || d < best!.1 { best = (y * gw + x, d) }
        } }
        return best?.0
    }
}

/// Binary min-heap of (cost, index).
struct MinHeap {
    private var items: [(Double, Int)] = []

    mutating func push(_ cost: Double, _ index: Int) {
        items.append((cost, index))
        var i = items.count - 1
        while i > 0 {
            let p = (i - 1) / 2
            if items[p].0 <= items[i].0 { break }
            items.swapAt(p, i); i = p
        }
    }

    mutating func pop() -> (Double, Int)? {
        guard !items.isEmpty else { return nil }
        let top = items[0]
        let last = items.removeLast()
        if !items.isEmpty {
            items[0] = last
            var i = 0
            while true {
                let l = 2 * i + 1, r = l + 1
                var m = i
                if l < items.count && items[l].0 < items[m].0 { m = l }
                if r < items.count && items[r].0 < items[m].0 { m = r }
                if m == i { break }
                items.swapAt(m, i); i = m
            }
        }
        return top
    }
}

/// Eight compass words for a bearing, screen-up being north.
public enum Compass {
    public static func word(_ bearing: Double) -> String {
        let names = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"]
        var degrees = bearing * 180 / .pi
        if degrees < 0 { degrees += 360 }
        return names[Int((degrees + 22.5) / 45) % 8]
    }
}
