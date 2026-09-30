// Line-for-line port of Sources/Cartography/Navigator.swift. Same algorithms, same
// constants, same iteration order; member names keep the Swift spelling.
global using AreaRule = SanctuarySonar.Cartography.Navigator.AreaRule;
using System.Globalization;

namespace SanctuarySonar.Cartography;

/// <summary>Where to go next on a stitched map, and which way to push to get there now.</summary>
public struct Guidance
{
    /// <summary>Direction to the next point on the route, radians clockwise from screen-up.</summary>
    public double bearing;
    /// <summary>Length of the whole route to the target, in minimap pixels.</summary>
    public double distance;
    /// <summary>The opening being led to, in canvas pixels.</summary>
    public Pt target;
    /// <summary>The point a short way along the route that the bearing aims at.</summary>
    public Pt carrot;
    /// <summary>The route, player first, in canvas pixels.</summary>
    public List<Pt> path;
    /// <summary>How many separate unexplored openings the map has.</summary>
    public int openings;
    /// <summary>The stitcher's <c>recentres</c> when this was planned: positions from different values
    /// are in different canvas coordinates and must not be compared.</summary>
    public int mapEpoch;
    /// <summary>The opening led to on the previous plan no longer exists — it was explored and
    /// closed. With a short route before it, that is a dead end.</summary>
    public bool previousClosed;
    /// <summary>Leading to a red objective mark (an enemy to slay, a target), not an opening.</summary>
    public bool toMark;
    /// <summary>How many red marks are remembered on the map.</summary>
    public int marks;
    /// <summary>Leading back to the spot the owner marked.</summary>
    public bool toSpot;
    /// <summary>Leading to an arch not yet walked through — an area's entrance.</summary>
    public bool toArch;
    /// <summary>Leading to unexplored ground beside a healing well, on the way to somewhere: the
    /// blind players' guide says the boss room is usually near one, and on 26 Sep the Tomb
    /// was a plain corridor north of the well, with no icon of its own.</summary>
    public bool toWell;
    /// <summary>Leading to an area already walked through, because the objective is to travel to it.</summary>
    public bool toArea;
    /// <summary>The opening was chosen with the objective marker's direction weighed in.</summary>
    public bool toMarker;
    /// <summary>The marker itself is on the map (inside the minimap, not pinned to its edge) and the
    /// route goes straight to it.</summary>
    public bool markerInView;
    /// <summary>No opening lies towards the marker but the ground that way is not wall: the lead is
    /// the marker's own bearing, straight on, until something stops it.</summary>
    public bool beeline;
    /// <summary>The other unexplored openings: straight-line bearing from the player and route
    /// length, for saying what else there is besides the one being led to.</summary>
    public List<(double bearing, double distance)> others;

    /// <summary>The Swift memberwise initialiser, same arguments in the same order; the last five
    /// are the defaulted <c>var</c>s.</summary>
    public Guidance(double bearing, double distance, Pt target, Pt carrot, List<Pt> path, int openings,
                    int mapEpoch, bool previousClosed, bool toMark, int marks, bool toSpot, bool toArch, bool toWell,
                    bool toArea = false, bool toMarker = false, bool markerInView = false, bool beeline = false,
                    List<(double bearing, double distance)>? others = null)
    {
        this.bearing = bearing; this.distance = distance; this.target = target; this.carrot = carrot;
        this.path = path; this.openings = openings; this.mapEpoch = mapEpoch; this.previousClosed = previousClosed;
        this.toMark = toMark; this.marks = marks; this.toSpot = toSpot; this.toArch = toArch; this.toWell = toWell;
        this.toArea = toArea; this.toMarker = toMarker; this.markerInView = markerInView; this.beeline = beeline;
        this.others = others ?? new List<(double bearing, double distance)>();
    }
}

/// <summary>
/// Frontier exploration (Yamauchi 1997) on the stitched minimap.
///
/// The map is reduced to a grid of <c>cell</c>-pixel squares: floor, wall, or never seen. An
/// <i>opening</i> is a connected run of floor cells next to never-seen cells — the edge of what
/// the minimap has shown, which is where a sighted player heads. The route is Dijkstra over
/// floor, costlier near walls so it keeps to the middle of a corridor, and the bearing aims at
/// a point <c>carrotDistance</c> along it rather than at the target: it says which way to go now,
/// around the corner, not in a straight line through the wall.
///
/// It keeps leading to the same opening until that opening is gone or another is much
/// nearer, so the beacon does not swing between two similar choices.
/// </summary>
public sealed class Navigator
{
    public const int cell = 4;
    /// <summary>Openings smaller than this many cells are noise at the edge of a reading.</summary>
    internal const int minimumOpening = 6;
    /// <summary>Openings nearer than this are already under the player's feet.</summary>
    internal const double minimumDistance = 60.0;
    internal const double carrotDistance = 60.0;
    /// <summary>A new opening replaces the current one only if its route is this much shorter, or
    /// scores <c>switchMargin</c> cells better.</summary>
    internal const double switchRatio = 0.6;
    internal const double switchMargin = 30.0;
    /// <summary>Pixels within which an opening counts as the one already being led to.</summary>
    internal const double sameOpening = 60.0;
    /// <summary>Each cell of an opening's width is worth this many cells of walking, up to
    /// <c>widthCap</c>: a corridor mouth is a likelier way on than a sliver at the edge of view.</summary>
    internal const double widthBonus = 1.5;
    internal const int widthCap = 25;
    /// <summary>Cells of walking each cell of distance away from the map's start is worth, capped.</summary>
    internal const double awayBonus = 0.3;
    internal const double awayCap = 60.0;
    /// <summary>Pixels round a gateway that are walls while staying in its area.</summary>
    internal const double gateBlock = 30.0;
    /// <summary>Cells added to an opening's score when it is in the area being left.</summary>
    internal const double leavePenalty = 400.0;
    /// <summary>While travelling, unexplored ground within this many pixels of a healing well is
    /// exempt from the leave penalty and scores this many cells better.</summary>
    internal const double wellRadius = 220.0;
    internal const double wellBonus = 150.0;
    /// <summary>Enclosed unseen pockets up to this many cells (≈ 40×40 px) are icons, filled as floor.</summary>
    internal const int iconPocket = 100;

    private Pt? currentTarget;
    /// <summary>Plans in a row on which the opening being led to was not found.</summary>
    private int missedPlans = 0;
    /// <summary>How many plans an opening may be missing before it counts as closed. An opening
    /// at the edge of what has been read flickers — present one frame, gone the next — and
    /// on the 25 Sep run each flicker switched the lead to another opening and back, 66
    /// times in 22 minutes. About a second at the live rate of ten plans a second.</summary>
    internal const int holdPlans = 8;
    /// <summary>A better opening must stay better for this many plans in a row before the lead moves
    /// to it — openings flicker in during combat (effects over the minimap) and each one
    /// pulled the lead away from a real target and back.</summary>
    internal const int switchPlans = 5;
    private (Pt point, int plans)? pending;
    private Pt? currentMark;
    private int markMissedPlans = 0;
    /// <summary>Set by MapLab to print the candidates of each plan.</summary>
    public Action<string>? debug;
    private int markSwitchPlans = 0;
    private int targetEpoch = 0;

    public Navigator() { }

    public void forget() { currentTarget = null; missedPlans = 0; pending = null; currentMark = null; markerHistory = new List<double>(); seenOpenings = new List<(Pt point, int plans)>(); }

    /// <summary>
    /// What the objective says about areas. <c>stay</c>: the objective is "… in the &lt;area&gt;" and
    /// the player is in it — offer nothing outside it, and never route back through a
    /// gateway out (the 26 Sep run was led out of the Ghastly Depths a second after its
    /// "slay all enemies" began). <c>leave</c>: the objective is to travel to another area —
    /// openings in this one score far worse, and only arches not yet used as gateways count.
    /// <c>goTo</c>: the objective names an area already walked through — lead to the nearest
    /// walked ground of it; with none reachable, leave <c>from</c> as <c>leave</c> would.
    /// </summary>
    // PORT NOTE: Swift `enum AreaRule: Equatable { case none, stay(Int), leave(Int), goTo(area:, from:) }`
    // becomes a readonly struct with a Kind and the two payload fields; equality is by value.
    // `ToString()` reproduces Swift's `\(rule)` description, which the debug line prints.
    public readonly struct AreaRule : IEquatable<AreaRule>
    {
        public enum Kind { none, stay, leave, goTo }
        public readonly Kind kind;
        public readonly int area;
        public readonly int? from;

        private AreaRule(Kind kind, int area, int? from) { this.kind = kind; this.area = area; this.from = from; }

        public static readonly AreaRule none = default;
        public static AreaRule stay(int area) => new AreaRule(Kind.stay, area, null);
        public static AreaRule leave(int area) => new AreaRule(Kind.leave, area, null);
        public static AreaRule goTo(int area, int? from) => new AreaRule(Kind.goTo, area, from);

        public bool Equals(AreaRule other) => kind == other.kind && area == other.area && from == other.from;
        public override bool Equals(object? obj) => obj is AreaRule other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(kind, area, from);
        public static bool operator ==(AreaRule a, AreaRule b) => a.Equals(b);
        public static bool operator !=(AreaRule a, AreaRule b) => !a.Equals(b);

        public override string ToString() => kind switch
        {
            Kind.stay => $"stay({area})",
            Kind.leave => $"leave({area})",
            Kind.goTo => $"goTo(area: {area}, from: {(from is int f ? $"Optional({f})" : "nil")})",
            _ => "none",
        };
    }

    /// <summary>Cells of walking an opening straight away from the objective marker costs, against
    /// one straight towards it (the cosine in between).</summary>
    public const double markerWeight = 200.0;
    /// <summary>With a marker showing, a better opening must beat the current one by this much (in
    /// cells) before the lead moves: the cost-ratio test is dropped, and without a larger
    /// margin the lead flickered between two openings at similar angles (Undercity replay,
    /// 27 Sep: 7 flip-flops in 2.5 min against 0 without the marker).</summary>
    internal const double markerSwitchMargin = 120.0;
    /// <summary>Beeline: how far ahead must be free of wall, and how far off the marker's bearing an
    /// opening may be and still count as "towards it".</summary>
    internal const double beelineLook = 60.0;
    /// <summary>With the marker pinned, an opening more than this far round from it is the player's
    /// back turned on the objective: walk at the marker instead (30 Sep 2026, floor 3 of the 07:29 run).</summary>
    internal static readonly double backOnMarkerAngle = 120.0 * Math.PI / 180;
    internal const double beelineStall = 20.0;
    internal const double beelineRest = 30.0;
    private double? beelineSince;
    private double beelineRestUntil = -1.0;
    internal const double beelineAngle = 50.0 * Math.PI / 180;
    /// <summary>Off: added on 27 Sep after the good runs and not yet shown to help on its own. The
    /// runs that reached floor 3 (09:16–10:05) had no beeline; measure before turning it on.</summary>
    public static bool beelineEnabled = false;
    /// <summary>The marker's bearing over the last plans, averaged: the pinned icon jitters.</summary>
    private List<double> markerHistory = new List<double>();
    internal const int markerSmoothing = 10;
    /// <summary>What the last plan led to: 0 an opening, 1 an area, 2 the marker on the map, 3 a red
    /// mark, 4 the spot. Coming back to openings from anything else is a change of target,
    /// not a dead end (the 07:35 run said "Dead end" at every switch).</summary>
    private int lastKind = 0;
    /// <summary>Openings seen on recent plans, by position. With a marker showing, an opening must
    /// have been there for <c>settledPlans</c> plans running before it can be chosen: the fog
    /// edge on the marker's side flickers, and each sliver that appeared won for a second
    /// and vanished (Undercity replay, 27 Sep: 8 flip-flops in 2.5 min; 0 without the marker).</summary>
    private List<(Pt point, int plans)> seenOpenings = new List<(Pt point, int plans)>();
    internal const int settledPlans = 5;

    /// <summary>Swift's <c>%.0f</c>: printf rounds an exact tie to even; .NET's "0" format does not
    /// promise that, so round first. Cost values such as 1.5 are exact ties.</summary>
    private static string F0(double v) => Math.Round(v, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>toMarks</c>: the objective is to slay something, so lead to the nearest red mark on
    /// the map when there is one, and to unexplored openings only when there is not.
    /// <c>marker</c>: the bearing of the objective marker pinned to the minimap's edge, when there
    /// is one — openings towards it score better, so the route heads for the objective
    /// rather than the nearest new ground (the Undercity is timed).
    /// </summary>
    public Guidance? plan(Stitcher map, bool toMarks = false, bool toSpot = false, bool toArches = false,
                          AreaRule areaRule = default, double? marker = null, Pt? markerPoint = null,
                          bool timed = false)
    {
        // Swift: `marker rawMarker: Double?` — the argument label is `marker`, the local is `rawMarker`.
        double? rawMarker = marker;
        if (map.player is not Pt player) return null;
        marker = null;
        if (rawMarker is double rawBearing)
        {
            markerHistory.Add(rawBearing);
            if (markerHistory.Count > markerSmoothing) markerHistory.RemoveAt(0);
            double hx = 0.0, hy = 0.0;
            foreach (var h in markerHistory) hx += Math.Cos(h);
            foreach (var h in markerHistory) hy += Math.Sin(h);
            marker = M.Atan2(hy, hx);
        }
        else
        {
            markerHistory = new List<double>();
        }
        if (map.recentres != targetEpoch) { currentTarget = null; targetEpoch = map.recentres; }
        int c = cell;
        var (minX, minY, maxX, maxY) = map.bounds;
        if (!(maxX > minX && maxY > minY)) return null;
        // One cell of never-seen margin all round, so the map's outer edge is an edge.
        int gx0 = minX - c, gy0 = minY - c;
        int gw = (maxX - minX) / c + 3, gh = (maxY - minY) / c + 3;
        int n = gw * gh;

        // 0 unknown, 1 floor, 2 wall — from the centre pixel of each cell.
        var state = new byte[n];
        for (int gy = 0; gy < gh; gy++) for (int gx = 0; gx < gw; gx++)
        {
            int px = gx0 + gx * c + c / 2, py = gy0 + gy * c + c / 2;
            if (!(px >= 0 && py >= 0 && px < map.size && py < map.size)) continue;
            int i = py * map.size + px;
            int seen = (int)map.seenVotes[i];
            if (!(seen > 0)) continue;
            state[gy * gw + gx] = Stitcher.state(seen, (int)map.floorVotes[i], (int)map.wallVotes[i]);
        }

        // Holes: never-seen cells that cannot be reached from the map's outer edge without
        // crossing seen ground. Every minimap icon (healing well, door, shrine) is ignored by
        // the reader and so leaves one of these, fixed to the map where the icon sits — and
        // as "never seen" beside floor, each read as an unexplored opening. On 25 Sep the
        // guide swung between a healing well and a door for five minutes. A hole is floor.
        var outside = new bool[n];
        var stack = new List<int>();
        for (int gx = 0; gx < gw; gx++) { stack.Add(gx); stack.Add((gh - 1) * gw + gx); }
        for (int gy = 0; gy < gh; gy++) { stack.Add(gy * gw); stack.Add(gy * gw + gw - 1); }
        while (stack.Count > 0)
        {
            int i = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1);
            if (!(!outside[i] && (state[i] == 0 || state[i] == 3))) continue;
            outside[i] = true;
            int gx = i % gw, gy = i / gw;
            if (gx > 0) stack.Add(i - 1);
            if (gx < gw - 1) stack.Add(i + 1);
            if (gy > 0) stack.Add(i - gw);
            if (gy < gh - 1) stack.Add(i + gw);
        }
        // Only icon-sized pockets. Since smooth darkness counts as unseen, the rock between
        // two corridors is an enclosed unseen pocket too, and filling it as floor let a
        // route cut straight through solid rock.
        var pocketSeen = new bool[n];
        for (int startCell = 0; startCell < n; startCell++)
        {
            if (!((state[startCell] == 0 || state[startCell] == 3) && !outside[startCell] && !pocketSeen[startCell])) continue;
            var pocket = new List<int>();
            var pocketStack = new List<int> { startCell };
            pocketSeen[startCell] = true;
            while (pocketStack.Count > 0)
            {
                int i = pocketStack[pocketStack.Count - 1]; pocketStack.RemoveAt(pocketStack.Count - 1);
                pocket.Add(i);
                int gx = i % gw, gy = i / gw;
                foreach (int j in new[] { gx > 0 ? i - 1 : -1, gx < gw - 1 ? i + 1 : -1, gy > 0 ? i - gw : -1, gy < gh - 1 ? i + gw : -1 })
                {
                    if (!(j >= 0 && (state[j] == 0 || state[j] == 3) && !outside[j] && !pocketSeen[j])) continue;
                    pocketSeen[j] = true;
                    pocketStack.Add(j);
                }
            }
            // Icon-sized: floor under an icon. Larger: rock between corridors, which the game
            // never reveals — wall, so it is neither routed through nor an "opening" beside
            // the floor (left unknown, every such pocket made false openings and doubled the
            // direction swings on the 25 Sep run).
            foreach (int i in pocket) state[i] = (byte)(pocket.Count <= iconPocket ? 1 : 2);
        }

        // Staying in an area: the gateways out of it are walls.
        if (areaRule.kind == AreaRule.Kind.stay)
        {
            int area = areaRule.area;
            // Not the gateway the player is standing in: walling it made the player's own
            // cell a wall with no floor within reach, and the plan returned nothing for the
            // 30 s they stood in the Ghastly Depths' doorway (26 Sep replay).
            foreach (var gate in map.gateways)
            {
                if (!(gate.areas.Contains(area)
                      && M.Hypot(gate.point.x - player.x, gate.point.y - player.y) > gateBlock * 2)) continue;
                int r = (int)gateBlock / c;
                int gx = ((int)gate.point.x - gx0) / c, gy = ((int)gate.point.y - gy0) / c;
                // PORT NOTE: Swift's closed ranges trap when lower > upper; C# just runs no iterations.
                for (int y = Math.Max(0, gy - r); y <= Math.Min(gh - 1, Math.Max(0, gy + r)); y++)
                    for (int x = Math.Max(0, gx - r); x <= Math.Min(gw - 1, Math.Max(0, gx + r)); x++)
                    {
                        if (!((x - gx) * (x - gx) + (y - gy) * (y - gy) <= r * r)) continue;
                        state[y * gw + x] = 2;
                    }
            }
        }

        // Distance to the nearest wall, in cells (chamfer, two passes). Never-seen cells are
        // not walls: an opening must not look like a wall to the router.
        int far = 1000;
        var wallDistance = new int[n];
        for (int i = 0; i < n; i++) wallDistance[i] = far;
        for (int i = 0; i < n; i++) if (state[i] == 2) wallDistance[i] = 0;
        for (int gy = 0; gy < gh; gy++) for (int gx = 0; gx < gw; gx++)
        {
            int i = gy * gw + gx;
            if (gx > 0) wallDistance[i] = Math.Min(wallDistance[i], wallDistance[i - 1] + 1);
            if (gy > 0) wallDistance[i] = Math.Min(wallDistance[i], wallDistance[i - gw] + 1);
        }
        for (int gy = gh - 1; gy >= 0; gy--) for (int gx = gw - 1; gx >= 0; gx--)
        {
            int i = gy * gw + gx;
            if (gx < gw - 1) wallDistance[i] = Math.Min(wallDistance[i], wallDistance[i + 1] + 1);
            if (gy < gh - 1) wallDistance[i] = Math.Min(wallDistance[i], wallDistance[i + gw] + 1);
        }

        // The player's cell, snapped to the nearest floor if the arrow sits on a wall pixel.
        int? start = cellIndex(player, gx0, gy0, gw, gh);
        if (start == null || state[start.Value] != 1)
        {
            start = nearestFloor(player, state, gx0, gy0, gw, gh, 6);
        }
        if (start is not int startIndex) return null;

        // Dijkstra over floor, 8-connected.
        var cost = new double[n];
        for (int i = 0; i < n; i++) cost[i] = double.PositiveInfinity;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = -1;
        var heap = new MinHeap();
        cost[startIndex] = 0;
        heap.push(0, startIndex);
        var steps = new (int, int, double)[] { (1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1),
                                                (1, 1, 1.414), (1, -1, 1.414), (-1, 1, 1.414), (-1, -1, 1.414) };
        while (heap.pop() is (double d, int i))
        {
            if (d > cost[i]) continue;
            int gx = i % gw, gy = i / gw;
            foreach (var (sx, sy, len) in steps)
            {
                int nx = gx + sx, ny = gy + sy;
                if (!(nx >= 0 && ny >= 0 && nx < gw && ny < gh)) continue;
                int j = ny * gw + nx;
                if (!(state[j] == 1)) continue;
                // Hugging a wall costs up to three times as much as the corridor's middle.
                double nearWall = 2.0 / (double)Math.Max(1, Math.Min(wallDistance[j], 4));
                double next = d + len * (1 + nearWall);
                if (next < cost[j]) { cost[j] = next; parent[j] = i; heap.push(next, j); }
            }
        }

        Pt point(int i)
        {
            return new Pt((double)(gx0 + (i % gw) * c + c / 2), (double)(gy0 + (i / gw) * c + c / 2));
        }
        int markCount = map.markMemory.marks.Count;
        Guidance guidance(int chosen, int openings, bool previousClosed, bool toMark, bool toSpot = false,
                          bool toArch = false, bool toWell = false, bool toArea = false,
                          List<(double bearing, double distance)>? others = null, bool toMarker = false)
        {
            // The route, player first.
            var route = new List<int>();
            int k = chosen;
            while (k >= 0) { route.Add(k); k = parent[k]; }
            route.Reverse();
            var path = new List<Pt>(route.Count);
            foreach (int r in route) path.Add(point(r));
            double walked = 0.0;
            Pt carrot = path[path.Count - 1];
            for (int idx = 1; idx < Math.Max(path.Count, 1); idx++)
            {
                walked += M.Hypot(path[idx].x - path[idx - 1].x, path[idx].y - path[idx - 1].y);
                if (walked >= carrotDistance) { carrot = path[idx]; break; }
            }
            double bearing = M.Atan2(carrot.x - player.x, -(carrot.y - player.y));
            double length = 0.0;
            for (int idx = 0; idx + 1 < path.Count; idx++)
                length += M.Hypot(path[idx + 1].x - path[idx].x, path[idx + 1].y - path[idx].y);
            return new Guidance(bearing: bearing, distance: length, target: point(chosen), carrot: carrot,
                                path: path, openings: openings, mapEpoch: map.recentres,
                                previousClosed: previousClosed, toMark: toMark, marks: markCount, toSpot: toSpot,
                                toArch: toArch, toWell: toWell, toArea: toArea, toMarker: toMarker, others: others ?? new List<(double bearing, double distance)>());
        }

        // Back to the marked spot, when asked: before marks and openings. If no route is
        // known yet it falls through, and the guide says so.
        if (toSpot && map.spot is Pt spot)
        {
            int? cellAt = cellIndex(spot, gx0, gy0, gw, gh);
            if (cellAt == null || state[cellAt.Value] != 1 || !double.IsFinite(cost[cellAt.Value]))
            {
                cellAt = nearestFloor(spot, state, gx0, gy0, gw, gh, 8);
            }
            if (cellAt is int spotCell && double.IsFinite(cost[spotCell]))
            {
                lastKind = 4;
                return guidance(spotCell, openings: 0, previousClosed: false, toMark: false, toSpot: true);
            }
        }

        // A red mark wins over any opening while the objective is to slay: route to the
        // floor cell nearest the reachable mark with the shortest route. A mark sitting on a
        // wall pixel or in a gap of the map snaps to floor within six cells.
        if (toMarks)
        {
            int? bestMark = null;
            var reachable = new List<(int cell, Pt point)>();
            foreach (var mark in map.markMemory.marks)
            {
                // A mark beyond the fog's edge is routed to the nearest reachable floor within
                // 60 px, not dropped: dropped, it blinked the lead between the mark and an
                // opening the other way, frame by frame (Mariner's Refuge, 26 Sep).
                int? cellAt = cellIndex(mark.point, gx0, gy0, gw, gh);
                if (cellAt == null || state[cellAt.Value] != 1 || !double.IsFinite(cost[cellAt.Value]))
                {
                    cellAt = nearestReachableFloor(mark.point, state, cost, gx0, gy0, gw, gh, 15);
                }
                if (!(cellAt is int markCell && double.IsFinite(cost[markCell]) && cost[markCell] * (double)c >= 12)) continue;
                reachable.Add((markCell, mark.point));
                if (bestMark == null || cost[markCell] < cost[bestMark.Value]) bestMark = markCell;
            }
            // Stay with the mark being led to while it exists, unless another is decisively
            // nearer for a while: enemies move, and the nearest one changed several times a
            // minute in combat on 26 Sep (102 of 148 direction swings were in mark-leading).
            // A mark that has just gone (or lost its route) is held for `holdPlans`, like an
            // opening: enemies' marks blink, and every blink was a swing of the beacon.
            if (bestMark == null && markMissedPlans < holdPlans && currentMark is Pt heldMark
                && nearestReachableFloor(heldMark, state, cost, gx0, gy0, gw, gh, 15) is int heldCell)
            {
                markMissedPlans += 1;
                lastKind = 3;
                return guidance(heldCell, openings: 0, previousClosed: false, toMark: true);
            }
            if (bestMark is int bestMarkCell)
            {
                markMissedPlans = 0;
                int chosenMark = bestMarkCell;
                if (currentMark is Pt held)
                {
                    // Swift `reachable.min(by:)`: the FIRST element with the least distance.
                    (int cell, Pt point)? same = null;
                    double sameDistance = 0;
                    foreach (var candidate in reachable)
                    {
                        double dist = M.Hypot(candidate.point.x - held.x, candidate.point.y - held.y);
                        if (same == null || dist < sameDistance) { same = candidate; sameDistance = dist; }
                    }
                    if (same is (int sameCell, Pt samePoint)
                        && M.Hypot(samePoint.x - held.x, samePoint.y - held.y) < 40)
                    {
                        bool decisive = cost[bestMarkCell] <= switchRatio * cost[sameCell];
                        markSwitchPlans = decisive ? markSwitchPlans + 1 : 0;
                        if (!decisive || markSwitchPlans < switchPlans) chosenMark = sameCell;
                    }
                }
                currentMark = null;
                foreach (var candidate in reachable) { if (candidate.cell == chosenMark) { currentMark = candidate.point; break; } }
                lastKind = 3;
                return guidance(chosenMark, openings: 0, previousClosed: false, toMark: true);
            }
            currentMark = null;
            markMissedPlans = 0;
        }

        // While the objective is to travel somewhere: an arch the player has not walked
        // through — nothing within the visited radius of the trail — before any opening.
        // Travelling: arches not yet used join the openings as candidates, weighed by route
        // like any other. They used to win outright, and on 26 Sep that led the owner 800 px
        // to the wrong arch for four minutes while the Tomb was a corridor beside the well.
        var archCells = new HashSet<int>();
        if (toArches)
        {
            foreach (var l in map.landmarkMemory.landmarks)
            {
                if (!(l.kind == Landmark.arch)) continue;
                // An arch already crossed from one area to another is not the way to a new one.
                bool crossed = false;
                foreach (var g in map.gateways) { if (M.Hypot(g.point.x - l.point.x, g.point.y - l.point.y) < 40) { crossed = true; break; } }
                if (crossed) continue;
                // Leaving an area, an arch walked past still counts — the Tomb's door may have
                // been passed on the way in. Otherwise one beside the trail was walked through.
                int vx = (int)l.point.x / Stitcher.visitCell, vy = (int)l.point.y / Stitcher.visitCell;
                if (areaRule.kind == AreaRule.Kind.leave) { }
                else if (vx >= 0 && vy >= 0 && vx < map.visitColumns && vy < map.visitColumns
                         && map.visited[vy * map.visitColumns + vx]) continue;
                int? cellAt = cellIndex(l.point, gx0, gy0, gw, gh);
                if (cellAt == null || state[cellAt.Value] != 1 || !double.IsFinite(cost[cellAt.Value]))
                {
                    cellAt = nearestFloor(l.point, state, gx0, gy0, gw, gh, 8);
                }
                if (!(cellAt is int archCell && double.IsFinite(cost[archCell]) && cost[archCell] * (double)c >= minimumDistance)) continue;
                archCells.Add(archCell);
            }
        }
        var wells = new List<Pt>();
        foreach (var l in map.landmarkMemory.landmarks) if (l.kind == Landmark.healingWell) wells.Add(l.point);
        bool nearWell(Pt p)
        {
            if (!toArches) return false;
            foreach (var w in wells) if (M.Hypot(w.x - p.x, w.y - p.y) <= wellRadius) return true;
            return false;
        }

        // Openings: floor cells beside never-seen cells, grouped by connectivity.
        var isFrontier = new bool[n];
        // Beside fog, or beside the flickering edge of it (state 3): demanding pure fog
        // left almost no openings, since the edge rings every fade.
        Func<int, bool> fog = k => state[k] == 0 || state[k] == 3;
        for (int gy = 1; gy < gh - 1; gy++) for (int gx = 1; gx < gw - 1; gx++)
        {
            int i = gy * gw + gx;
            if (!(state[i] == 1 && double.IsFinite(cost[i]))) continue;
            // Beside the trail nothing is unexplored: the game revealed it as the player
            // passed. A ledge the owner walked along read as an opening until this.
            int px = gx0 + gx * c + c / 2, py = gy0 + gy * c + c / 2;
            if (px >= 0 && py >= 0 && px < map.size && py < map.size
                && map.visited[(py / Stitcher.visitCell) * map.visitColumns + px / Stitcher.visitCell]) continue;
            if (fog(i - 1) || fog(i + 1) || fog(i - gw) || fog(i + gw))
            {
                isFrontier[i] = true;
            }
        }
        var group = new int[n];
        for (int i = 0; i < n; i++) group[i] = -1;
        var openings = new List<(int nearest, int size, List<int> cells)>();
        for (int i = 0; i < n; i++)
        {
            if (!(isFrontier[i] && group[i] < 0)) continue;
            var groupStack = new List<int> { i };
            int members = 0, nearest = i;
            var cells = new List<int>();
            group[i] = openings.Count;
            while (groupStack.Count > 0)
            {
                int k = groupStack[groupStack.Count - 1]; groupStack.RemoveAt(groupStack.Count - 1);
                members += 1;
                cells.Add(k);
                if (cost[k] < cost[nearest]) nearest = k;
                int kx = k % gw, ky = k / gw;
                foreach (var (sx, sy, _) in steps)
                {
                    int nx = kx + sx, ny = ky + sy;
                    if (!(nx >= 0 && ny >= 0 && nx < gw && ny < gh)) continue;
                    int j = ny * gw + nx;
                    if (isFrontier[j] && group[j] < 0) { group[j] = openings.Count; groupStack.Add(j); }
                }
            }
            openings.Add((nearest, members, cells));
        }
        double pixelsPerCost = (double)c;
        var usable = new List<(int nearest, int size, List<int> cells)>();
        foreach (var o in openings)
        {
            if (o.size >= minimumOpening && cost[o.nearest] * pixelsPerCost >= minimumDistance) usable.Add(o);
        }
        // PORT NOTE: Swift iterates `archCells` (a Set) in hash order, which is not stable across
        // processes; C#'s HashSet gives insertion order. Either order is "an" order the Swift
        // code could have produced; ties between arch-only candidates may resolve differently.
        foreach (int archCell in archCells)
        {
            bool already = false;
            foreach (var o in usable) { if (o.cells.Contains(archCell)) { already = true; break; } }
            if (already) continue;
            usable.Add((archCell, widthCap, new List<int> { archCell }));
        }
        if (areaRule.kind == AreaRule.Kind.stay)
        {
            int area = areaRule.area;
            usable.RemoveAll(o =>
            {
                var p = new Pt((double)(gx0 + (o.nearest % gw) * c + c / 2), (double)(gy0 + (o.nearest / gw) * c + c / 2));
                if (areaOfOpening(p, map) is int a && a != area) return true;
                return false;
            });
        }
        // The objective marker inside the minimap (the boss room in view, 27 Sep, third
        // Undercity run, with 26 s left): straight to the nearest floor with a route to it.
        // Only when there is reachable floor right by it (4 cells): a marker across water or a
        // wall had "reachable floor" 80 px away on the near side, and the route ran at the
        // wall (27 Sep, Ziggurat District). Otherwise the openings are weighed by how near
        // they are to the marker's point, below.
        if (markerPoint is Pt markerPt
            && nearestReachableFloor(markerPt, state, cost, gx0, gy0, gw, gh, 20) is int markerCell)
        {
            lastKind = 2;
            currentTarget = point(markerCell);
            var others = new List<(double bearing, double distance)>();
            foreach (var o in usable)
            {
                var p = point(o.nearest);
                others.Add((M.Atan2(p.x - player.x, -(p.y - player.y)), cost[o.nearest] * pixelsPerCost));
            }
            var g = guidance(markerCell, openings: usable.Count, previousClosed: false, toMark: false, others: others, toMarker: true);
            g.markerInView = true;
            return g;
        }
        // Travelling to an area already walked through: the nearest ground stamped with it
        // that a route reaches — the border crossed on the way out, in practice.
        AreaRule rule = areaRule;
        if (rule.kind == AreaRule.Kind.goTo)
        {
            int wanted = rule.area; int? @from = rule.from;
            int nv = map.visitColumns, vc = Stitcher.visitCell;
            int? bestGo = null;
            for (int vy = Math.Max(0, minY / vc); vy <= Math.Min(nv - 1, maxY / vc); vy++)
                for (int vx = Math.Max(0, minX / vc); vx <= Math.Min(nv - 1, maxX / vc); vx++)
                {
                    if (!(map.areaAt[vy * nv + vx] == (byte)(wanted + 1))) continue;
                    var p = new Pt((double)(vx * vc + vc / 2), (double)(vy * vc + vc / 2));
                    if (!(cellIndex(p, gx0, gy0, gw, gh) is int i && state[i] == 1 && double.IsFinite(cost[i])
                          && cost[i] * (double)c >= minimumDistance)) continue;
                    if (bestGo == null || cost[i] < cost[bestGo.Value]) bestGo = i;
                }
            if (bestGo is int bestArea)
            {
                lastKind = 1;
                currentTarget = point(bestArea);
                var others = new List<(double bearing, double distance)>();
                foreach (var o in usable)
                {
                    var p = point(o.nearest);
                    others.Add((M.Atan2(p.x - player.x, -(p.y - player.y)), cost[o.nearest] * pixelsPerCost));
                }
                return guidance(bestArea, openings: usable.Count, previousClosed: false, toMark: false, toArea: true, others: others);
            }
            rule = @from is int fromArea ? AreaRule.leave(fromArea) : AreaRule.none;
        }
        // How long each opening has been about, by position; under a marker, only settled
        // ones (and the one already being led to) may be chosen.
        var stillSeen = new List<(Pt point, int plans)>();
        var settled = new bool[usable.Count];
        for (int k = 0; k < usable.Count; k++)
        {
            var o = usable[k];
            var p = point(o.nearest);
            // Swift `seenOpenings.first { … }?.plans ?? 0`: the first within range.
            int previousPlans = 0;
            foreach (var s in seenOpenings)
            {
                if (M.Hypot(s.point.x - p.x, s.point.y - p.y) < sameOpening) { previousPlans = s.plans; break; }
            }
            int plans = previousPlans + 1;
            stillSeen.Add((p, plans));
            settled[k] = plans >= settledPlans;
        }
        seenOpenings = stillSeen;
        if (marker != null)
        {
            var keep = new List<int>();
            for (int k = 0; k < usable.Count; k++)
            {
                bool nearCurrent = false;
                if (currentTarget is Pt t)
                {
                    foreach (int cellOf in usable[k].cells)
                    {
                        if (M.Hypot(point(cellOf).x - t.x, point(cellOf).y - t.y) < sameOpening) { nearCurrent = true; break; }
                    }
                }
                if (settled[k] || nearCurrent) keep.Add(k);
            }
            if (keep.Count > 0)
            {
                var kept = new List<(int nearest, int size, List<int> cells)>(keep.Count);
                foreach (int k in keep) kept.Add(usable[k]);
                usable = kept;
            }
        }
        if (lastKind != 0) { currentTarget = null; missedPlans = 0; pending = null; lastKind = 0; }
        // Straight at the marker when nothing else serves. Standing at the fog's edge pushing
        // towards it, the new ground is within the walked radius and is no opening, so the
        // nearest opening was behind or beside the player (the 10:33 run: marker west, lead
        // north for a minute). A sighted player walks at the marker until a wall stops them;
        // so does this, if the next `beelineLook` px that way hold no wall.
        Guidance? beeline(double bearing)
        {
            Pt last = player;
            for (double d = 8.0; d <= beelineLook; d += 8)
            {
                var p = new Pt(player.x + Math.Sin(bearing) * d, player.y - Math.Cos(bearing) * d);
                if (!(cellIndex(p, gx0, gy0, gw, gh) is int i && state[i] != 2)) return null;
                last = p;
            }
            currentTarget = null;
            var g = new Guidance(bearing: bearing, distance: beelineLook, target: last, carrot: last, path: new List<Pt> { player, last },
                                 openings: usable.Count, mapEpoch: map.recentres, previousClosed: false, toMark: false,
                                 marks: markCount, toSpot: false, toArch: false, toWell: false);
            g.toMarker = true;
            g.beeline = true;
            var others = new List<(double bearing, double distance)>();
            foreach (var o in usable)
            {
                var p = point(o.nearest);
                others.Add((M.Atan2(p.x - player.x, -(p.y - player.y)), cost[o.nearest] * pixelsPerCost));
            }
            g.others = others;
            return g;
        }
        if (beelineEnabled && marker is double markerBearing)
        {
            bool anyTowards = false;
            foreach (var o in usable)
            {
                var p = point(o.nearest);
                double d = Math.Abs(M.Atan2(p.x - player.x, -(p.y - player.y)) - markerBearing) % (2 * Math.PI);
                if (d > Math.PI) d = 2 * Math.PI - d;
                if (d < beelineAngle) { anyTowards = true; break; }
            }
            if (!anyTowards && beeline(markerBearing) is Guidance bg)
            {
                lastKind = 5;
                return bg;
            }
        }
        bool hadTarget = currentTarget != null;
        // No opening anywhere, but the objective marker is known: point at it rather than go
        // silent (30 Sep 2026, floor 2 of the Ziggurat District: marker on the map, unreachable
        // over known floor, frontier closed, beacon silent for 45 s). Walls are the steering's business.
        if (usable.Count == 0 && (markerPoint is Pt mp ? (double?)M.Atan2(mp.x - player.x, -(mp.y - player.y)) : marker) is double lastBearing)
        {
            double lastDistance = markerPoint is Pt mp2 ? M.Hypot(mp2.x - player.x, mp2.y - player.y) : beelineLook;
            var end = new Pt(player.x + Math.Sin(lastBearing) * lastDistance, player.y - Math.Cos(lastBearing) * lastDistance);
            currentTarget = null;
            lastKind = 5;
            var lg = new Guidance(bearing: lastBearing, distance: lastDistance, target: end, carrot: end, path: new List<Pt> { player, end },
                                  openings: 0, mapEpoch: map.recentres, previousClosed: false, toMark: false,
                                  marks: markCount, toSpot: false, toArch: false, toWell: false);
            lg.toMarker = true;
            lg.beeline = true;
            lg.markerInView = markerPoint != null;
            return lg;
        }
        if (usable.Count == 0) { currentTarget = null; return null; }

        // Dungeons follow a formula (TJ the Blind Gamer's guide): you start in one part of
        // the map and the objectives and the boss are in others. So an opening farther from
        // where this map began than the player is now scores a little better — gently, so
        // a much nearer opening still wins.
        Pt origin = map.trail.Count > 0 ? map.trail[0].point : player;
        double playerReach = M.Hypot(player.x - origin.x, player.y - origin.y);
        double score((int nearest, int size, List<int> cells) o)
        {
            var p = point(o.nearest);
            double onward = (M.Hypot(p.x - origin.x, p.y - origin.y) - playerReach) / (double)c;
            double s = cost[o.nearest] - widthBonus * (double)Math.Min(o.size, widthCap)
                - awayBonus * Math.Max(-awayCap, Math.Min(awayCap, onward));
            if (nearWell(p) && !timed)
            {
                s -= wellBonus;
            }
            else if (rule.kind == AreaRule.Kind.leave && areaOfOpening(p, map) == rule.area)
            {
                s += leavePenalty;
            }
            if (marker is double mk)
            {
                double towards = M.Atan2(p.x - player.x, -(p.y - player.y));
                s += markerWeight * (1 - Math.Cos(towards - mk));
            }
            return s;
        }
        if (debug is { } debugList)
        {
            var list = new List<string>();
            foreach (var o in usable)
            {
                var p = point(o.nearest);
                int? areaOf = areaOfOpening(p, map);
                list.Add($"({F0(p.x)},{F0(p.y)}) cost {F0(cost[o.nearest])} size {o.size} well {(nearWell(p) ? "Y" : "n")} area {(areaOf is int ao ? ao.ToString(CultureInfo.InvariantCulture) : "-")} arch {(archCells.Contains(o.nearest) ? "Y" : "n")} score {F0(score(o))}");
            }
            var wellWords = new List<string>();
            foreach (var w in wells) wellWords.Add($"\"({(int)w.x},{(int)w.y})\"");
            // Swift prints `[String]` as `["(1,2)", "(3,4)"]` and an empty one as `[]`.
            debugList($"player ({(int)player.x},{(int)player.y}) wells [{string.Join(", ", wellWords)}] rule {rule} candidates: " + string.Join(" | ", list));
        }
        // Swift `usable.min { score($0) < score($1) }!`: the FIRST element with the least score.
        var best = usable[0];
        double bestScore = score(best);
        for (int k = 1; k < usable.Count; k++)
        {
            double sk = score(usable[k]);
            if (sk < bestScore) { best = usable[k]; bestScore = sk; }
        }
        int chosen = best.nearest;
        bool previousClosed = false;
        if (currentTarget is Pt previous)
        {
            // The same opening is whichever one has any cell near where the last target was:
            // an opening's nearest cell slides along it as the player moves, so comparing
            // only nearest cells (the first version) lost it every few steps and swung the
            // beacon between two openings.
            double gap((int nearest, int size, List<int> cells) o)
            {
                // Swift `lazy.map { … }.min() ?? .infinity`.
                double least = double.PositiveInfinity;
                bool any = false;
                foreach (int cellOf in o.cells)
                {
                    double dist = M.Hypot(point(cellOf).x - previous.x, point(cellOf).y - previous.y);
                    if (!any || dist < least) { least = dist; any = true; }
                }
                return any ? least : double.PositiveInfinity;
            }
            // The nearest, not the first within reach: where openings sit shoulder to shoulder
            // round a small explored patch (the Undercity, 27 Sep), "first within 60 px" was a
            // neighbour of the target, and the target walked along the chain plan by plan.
            // Swift `usable.min(by: { gap($0) < gap($1) })`: the FIRST element with the least gap.
            (int nearest, int size, List<int> cells)? sameOpt = null;
            double sameGap = 0;
            foreach (var o in usable)
            {
                double gk = gap(o);
                if (sameOpt == null || gk < sameGap) { sameOpt = o; sameGap = gk; }
            }
            if (sameOpt is { } same && gap(same) < sameOpening)
            {
                missedPlans = 0;
                // Stay unless the best is decisively nearer, and has been for a while.
                bool decisive = marker != null
                    ? score(best) <= score(same) - markerSwitchMargin
                    : score(best) <= score(same) - switchMargin && cost[best.nearest] <= switchRatio * cost[same.nearest];
                if (decisive)
                {
                    var p = point(best.nearest);
                    if (pending is (Pt waitingPoint, int waitingPlans) && M.Hypot(waitingPoint.x - p.x, waitingPoint.y - p.y) < sameOpening)
                    {
                        pending = (p, waitingPlans + 1);
                    }
                    else
                    {
                        pending = (p, 1);
                    }
                }
                if (!decisive || (pending?.plans ?? 0) < switchPlans)
                {
                    chosen = same.nearest;
                    if (!decisive) pending = null;
                }
                else
                {
                    pending = null;
                }
            }
            else
            {
                int? held = null;
                if (missedPlans < holdPlans)
                {
                    int? at = cellIndex(previous, gx0, gy0, gw, gh);
                    held = at is int h0 && state[h0] == 1 && double.IsFinite(cost[h0]) ? h0 : (int?)null;
                    if (held == null)
                    {
                        int? nf = nearestFloor(previous, state, gx0, gy0, gw, gh, 5);
                        held = nf is int h1 && double.IsFinite(cost[h1]) ? h1 : (int?)null;
                    }
                }
                if (missedPlans < holdPlans && held is int heldOpening)
                {
                    // Missing, but not for long: keep leading to where it was.
                    missedPlans += 1;
                    chosen = heldOpening;
                }
                else
                {
                    missedPlans = 0;
                    previousClosed = true;
                }
            }
        }
        if (debug is { } debugChoice)
        {
            Pt cp = point(chosen), bp = point(best.nearest);
            string previousWord = currentTarget is Pt ct ? $"({(int)ct.x},{(int)ct.y})" : "-";
            debugChoice($"CHOICE ({F0(cp.x)},{F0(cp.y)}) best ({F0(bp.x)},{F0(bp.y)}) previous {previousWord} pending {pending?.plans ?? 0} missed {missedPlans} closed {(previousClosed ? "Y" : "n")} usable {usable.Count}");
        }
        // Do not turn the player's back on a pinned objective marker (see backOnMarkerAngle).
        if (marker is double pinned && markerPoint == null && map.trail.Count > 0)
        {
            double now = map.trail[map.trail.Count - 1].time;
            var cp2 = point(chosen);
            double d = Math.Abs(M.Atan2(cp2.x - player.x, -(cp2.y - player.y)) - pinned) % (2 * Math.PI);
            if (d > Math.PI) d = 2 * Math.PI - d;
            if (d > backOnMarkerAngle && now >= beelineRestUntil)
            {
                beelineSince ??= now;
                Pt? earlierPoint = null;
                for (int k = map.trail.Count - 1; k >= 0; k--) { if (now - map.trail[k].time >= beelineStall) { earlierPoint = map.trail[k].point; break; } }
                if (now - beelineSince.Value >= beelineStall && earlierPoint is Pt ep && M.Hypot(player.x - ep.x, player.y - ep.y) < 20)
                {
                    beelineRestUntil = now + beelineRest;
                    beelineSince = null;
                }
                else
                {
                    var end = new Pt(player.x + Math.Sin(pinned) * beelineLook, player.y - Math.Cos(pinned) * beelineLook);
                    currentTarget = null;
                    lastKind = 5;
                    var bg2 = new Guidance(bearing: pinned, distance: beelineLook, target: end, carrot: end, path: new List<Pt> { player, end },
                                           openings: usable.Count, mapEpoch: map.recentres, previousClosed: false, toMark: false,
                                           marks: markCount, toSpot: false, toArch: false, toWell: false);
                    bg2.toMarker = true;
                    bg2.beeline = true;
                    var oth = new List<(double bearing, double distance)>();
                    foreach (var o in usable) { var q = point(o.nearest); oth.Add((M.Atan2(q.x - player.x, -(q.y - player.y)), cost[o.nearest] * pixelsPerCost)); }
                    bg2.others = oth;
                    return bg2;
                }
            }
            else beelineSince = null;
        }
        else beelineSince = null;
        currentTarget = point(chosen);
        var othersLeft = new List<(double bearing, double distance)>();
        foreach (var o in usable)
        {
            if (o.nearest == chosen) continue;
            var p = point(o.nearest);
            othersLeft.Add((M.Atan2(p.x - player.x, -(p.y - player.y)), cost[o.nearest] * pixelsPerCost));
        }
        return guidance(chosen, openings: usable.Count, previousClosed: previousClosed && hadTarget, toMark: false,
                        toArch: archCells.Contains(chosen), toWell: nearWell(point(chosen)) && !timed, others: othersLeft,
                        toMarker: marker != null);
    }

    /// <summary>The area a point belongs to: the stamp of the nearest walked cell, within 160 px.</summary>
    private int? areaOfOpening(Pt p, Stitcher map)
    {
        int n = map.visitColumns, vx = (int)p.x / Stitcher.visitCell, vy = (int)p.y / Stitcher.visitCell;
        for (int r = 0; r <= 40; r++)
        {
            for (int y = Math.Max(0, vy - r); y <= Math.Min(n - 1, vy + r); y++)
                for (int x = Math.Max(0, vx - r); x <= Math.Min(n - 1, vx + r); x++)
                {
                    if (!(Math.Abs(x - vx) == r || Math.Abs(y - vy) == r)) continue;
                    byte a = map.areaAt[y * n + x];
                    if (a > 0) return (int)a - 1;
                }
        }
        return null;
    }

    private int? cellIndex(Pt p, int gx0, int gy0, int gw, int gh)
    {
        int gx = ((int)p.x - gx0) / cell, gy = ((int)p.y - gy0) / cell;
        if (!(gx >= 0 && gy >= 0 && gx < gw && gy < gh)) return null;
        return gy * gw + gx;
    }

    /// <summary>The nearest floor cell with a route from the player, within <c>within</c> cells.</summary>
    private int? nearestReachableFloor(Pt p, byte[] state, double[] cost, int gx0, int gy0, int gw, int gh, int within)
    {
        int cx = ((int)p.x - gx0) / cell, cy = ((int)p.y - gy0) / cell;
        (int index, int d)? best = null;
        for (int dy = -within; dy <= within; dy++) for (int dx = -within; dx <= within; dx++)
        {
            int x = cx + dx, y = cy + dy;
            if (!(x >= 0 && y >= 0 && x < gw && y < gh && state[y * gw + x] == 1 && double.IsFinite(cost[y * gw + x]))) continue;
            int d = dx * dx + dy * dy;
            if (best == null || d < best.Value.d) best = (y * gw + x, d);
        }
        return best?.index;
    }

    private int? nearestFloor(Pt p, byte[] state, int gx0, int gy0, int gw, int gh, int within)
    {
        int cx = ((int)p.x - gx0) / cell, cy = ((int)p.y - gy0) / cell;
        (int index, int d)? best = null;
        for (int dy = -within; dy <= within; dy++) for (int dx = -within; dx <= within; dx++)
        {
            int x = cx + dx, y = cy + dy;
            if (!(x >= 0 && y >= 0 && x < gw && y < gh && state[y * gw + x] == 1)) continue;
            int d = dx * dx + dy * dy;
            if (best == null || d < best.Value.d) best = (y * gw + x, d);
        }
        return best?.index;
    }
}

/// <summary>Binary min-heap of (cost, index).</summary>
// PORT NOTE: a Swift mutating struct; a class here, since it is only ever a single local.
internal sealed class MinHeap
{
    private readonly List<(double, int)> items = new List<(double, int)>();

    public void push(double cost, int index)
    {
        items.Add((cost, index));
        int i = items.Count - 1;
        while (i > 0)
        {
            int p = (i - 1) / 2;
            if (items[p].Item1 <= items[i].Item1) break;
            (items[p], items[i]) = (items[i], items[p]); i = p;
        }
    }

    public (double, int)? pop()
    {
        if (items.Count == 0) return null;
        var top = items[0];
        var last = items[items.Count - 1];
        items.RemoveAt(items.Count - 1);
        if (items.Count > 0)
        {
            items[0] = last;
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1;
                int m = i;
                if (l < items.Count && items[l].Item1 < items[m].Item1) m = l;
                if (r < items.Count && items[r].Item1 < items[m].Item1) m = r;
                if (m == i) break;
                (items[m], items[i]) = (items[i], items[m]); i = m;
            }
        }
        return top;
    }
}

/// <summary>Eight compass words for a bearing, screen-up being north.</summary>
public static class Compass
{
    public static string word(double bearing)
    {
        string[] names = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
        double degrees = bearing * 180 / Math.PI;
        if (degrees < 0) degrees += 360;
        return names[(int)((degrees + 22.5) / 45) % 8];
    }
}
