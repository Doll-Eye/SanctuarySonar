// Port of Sources/Cartography/Stitcher.swift. Line for line: same constants, same
// algorithms, same member names (lower camel case) as the Swift source.
namespace SanctuarySonar.Cartography;

/// <summary>
/// Builds one map of the dungeon from minimap readings that scroll under the player.
///
/// Each reading is placed where it best agrees with everything placed so far (not just the
/// previous frame, so small errors do not accumulate), searched over a window of shifts
/// around the last placement. The minimap never rotates or zooms, so a shift is all there is.
/// A reading that agrees poorly everywhere is not placed and does not vote — a wrong vote is
/// worse than a missing one.
///
/// Not thread-safe: one queue owns it.
/// </summary>
public sealed class Stitcher
{
    public readonly int size;
    /// <summary>
    /// Per canvas pixel: frames that showed it as floor or wall, as floor, and as wall.
    /// Smooth darkness never votes and votes are never forgotten: a scheme that let fog vote
    /// and forgot old votes (tried 26 Sep) flickered at the edge of the fade and doubled or
    /// trebled the direction swings, however the thresholds were set.
    ///
    /// What keeps a spell's glow off the map instead: the game reveals nothing new beyond
    /// <c>revealRadius</c> of the player. A pixel never seen before that reads as floor or
    /// wall farther away than that is neither. A pixel already on the map keeps voting at
    /// any distance.
    /// </summary>
    public ushort[] floorVotes { get; private set; }
    public ushort[] seenVotes { get; private set; }
    public ushort[] wallVotes { get; private set; }
    /// <summary>Minimap pixels. The fade sits at about 120–140 px in the 26 Sep frames.</summary>
    public const double revealRadius = 150.0;
    /// <summary>Where the current reading's top-left sits on the canvas.</summary>
    public (int x, int y) offset { get; private set; }
    /// <summary>Every placed arrow position, oldest first, in canvas pixels, with its time.</summary>
    public List<(Pt point, double time)> trail { get; private set; } = new List<(Pt point, double time)>();
    /// <summary>The seen part of the canvas.</summary>
    public (int minX, int minY, int maxX, int maxY) bounds { get; private set; }
    public bool isEmpty { get; private set; } = true;

    /// <summary>
    /// Where the player has been, on a grid of <c>visitCell</c> pixels: every cell within
    /// <c>visitRadius</c> of any trail point. The game reveals the floor round the player as
    /// they walk, so nothing this close to the trail can still be unexplored — dark there
    /// is not floor. Kept here, stamped once per step, because the trail runs to thousands
    /// of points and the navigator plans ten times a second.
    /// </summary>
    public const int visitCell = 4;
    /// <summary>
    /// 30, not 60: at 60 the mouth of the corridor to the Tomb, 50 px off the trail, could
    /// never be an opening. Ledges beside the trail are sealed by the sharp-edge rule now.
    /// </summary>
    public const int visitRadius = 30;
    public bool[] visited { get; private set; }
    /// <summary>
    /// Which area each visited cell was walked in: 0 unknown, else index + 1 into
    /// <c>areaNames</c>. Stamped with the area name read above the minimap.
    /// </summary>
    public byte[] areaAt { get; private set; }
    public List<string> areaNames { get; private set; } = new List<string>();
    public int? currentArea { get; private set; }
    /// <summary>
    /// Where the player crossed from one area to another: the arch used, if one was known
    /// near, else the spot itself. An arch that is a gateway is not "the way in" to
    /// anywhere new, and a gateway out is what "stay in this area" must not route through.
    /// </summary>
    public List<(Pt point, HashSet<int> areas)> gateways { get; private set; } = new List<(Pt point, HashSet<int> areas)>();
    // PORT NOTE: MarkMemory and LandmarkMemory are Swift structs mutated in place through
    // `mutating` methods. They are kept in private fields here and exposed read-only, so the
    // mutation reaches the stored value whether the port makes them structs or classes.
    private MarkMemory _markMemory = new MarkMemory();
    /// <summary>Red objective marks, remembered in canvas pixels.</summary>
    public MarkMemory markMemory => _markMemory;
    /// <summary>
    /// A place the owner marked to come back to (an altar, a pedestal) — what blind
    /// players otherwise do by dropping an item there to hear it later.
    /// </summary>
    public Pt? spot { get; private set; }
    private LandmarkMemory _landmarkMemory = new LandmarkMemory();
    /// <summary>Healing wells and arches seen on this map.</summary>
    public LandmarkMemory landmarkMemory => _landmarkMemory;
    /// <summary>Landmarks confirmed by the last placed reading — for the guide to announce.</summary>
    public (Landmark, Pt)[] newLandmarks { get; private set; } = new (Landmark, Pt)[0];

    /// <summary>Marks where the player is now. False when there is no position yet.</summary>
    public bool markSpot()
    {
        if (player is not Pt p) return false;
        spot = p;
        return true;
    }
    private Pt? lastStamp;

    public const int searchRadius = 28;
    public const double minimumScore = 0.35;

    public Stitcher(int size = 3000)
    {
        this.size = size;
        floorVotes = new ushort[size * size];
        seenVotes = new ushort[size * size];
        wallVotes = new ushort[size * size];
        offset = (size / 2, size / 2);
        bounds = (size, size, 0, 0);
        visited = new bool[(size / visitCell) * (size / visitCell)];
        areaAt = new byte[(size / visitCell) * (size / visitCell)];
    }

    public int? areaIndex(string name)
    {
        // PORT NOTE: Swift's caseInsensitiveCompare is a Unicode case-insensitive compare;
        // InvariantCultureIgnoreCase is the nearest .NET equivalent.
        int i = areaNames.FindIndex(a => string.Equals(a, name, StringComparison.InvariantCultureIgnoreCase));
        return i >= 0 ? i : null;
    }

    /// <summary>
    /// The known area an objective's destination names ("Siren's Chamber", read with a
    /// letter or two wrong, or as part of a longer phrase), if any.
    /// </summary>
    public int? areaMatching(string name)
    {
        // Never "the destination contains the area's name": with the recogniser failing,
        // "Cave District" was also read as "District", which "District Boss" contains, and
        // the guide led round in circles between the marker and that patch (27 Sep, 07:35).
        // PORT NOTE: lowercased() → ToLowerInvariant(); and C#'s Contains("") is true where
        // Swift's String.contains("") may differ — only matters for an empty name.
        string wanted = name.ToLowerInvariant();
        int i = areaNames.FindIndex(a =>
        {
            string known = a.ToLowerInvariant();
            return known == wanted || known.Contains(wanted) || Objective.alike(known, wanted);
        });
        return i >= 0 ? i : null;
    }

    /// <summary>The area name above the minimap changed (or was read for the first time).</summary>
    public void setArea(string name)
    {
        int index;
        if (areaIndex(name) is int found) index = found;
        else { areaNames.Add(name); index = areaNames.Count - 1; }
        if (index == currentArea) return;
        if (currentArea is int previous && player is Pt p)
        {
            // The nearest arch within 150 px, first one kept on a tie (Swift's min(by:)).
            Pt? arch = null;
            double archDistance = 0;
            foreach (var l in landmarkMemory.landmarks)
            {
                if (l.kind != Landmark.arch) continue;
                double d = M.Hypot(l.point.x - p.x, l.point.y - p.y);
                if (!(d < 150)) continue;
                if (arch == null || d < archDistance) { arch = l.point; archDistance = d; }
            }
            gateways.Add((arch ?? p, new HashSet<int> { previous, index }));
        }
        currentArea = index;
        // The ground round the player was stamped with the old area on the way in, so the
        // new area's own openings read as outside it and were dropped: 30 s of "no opening"
        // on entering the Ghastly Depths (26 Sep replay). Re-stamp here as the new area.
        if (player is Pt q) { lastStamp = null; stampVisit(q); }
    }

    public int visitColumns => size / visitCell;

    private void stampVisit(Pt p)
    {
        if (lastStamp is Pt last && M.Hypot(p.x - last.x, p.y - last.y) < (double)visitCell) return;
        lastStamp = p;
        int n = visitColumns, c = visitCell, r = visitRadius / c;
        int cx = (int)p.x / c, cy = (int)p.y / c;
        byte stamp = (byte)Math.Min(254, (currentArea ?? -1) + 1);
        for (int y = Math.Max(0, cy - r); y <= Math.Min(n - 1, cy + r); y++)
        {
            for (int x = Math.Max(0, cx - r); x <= Math.Min(n - 1, cx + r); x++)
            {
                if (!((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r)) continue;
                visited[y * n + x] = true;
                if (stamp > 0) areaAt[y * n + x] = stamp;
            }
        }
    }

    public void reset()
    {
        for (int i = 0; i < floorVotes.Length; i++) { floorVotes[i] = 0; seenVotes[i] = 0; wallVotes[i] = 0; }
        offset = (size / 2, size / 2);
        trail = new List<(Pt point, double time)>();
        for (int i = 0; i < areaAt.Length; i++) areaAt[i] = 0;
        gateways = new List<(Pt point, HashSet<int> areas)>();
        _markMemory.clear();
        _landmarkMemory.clear();
        newLandmarks = new (Landmark, Pt)[0];
        spot = null;
        for (int i = 0; i < visited.Length; i++) visited[i] = false;
        lastStamp = null;
        bounds = (size, size, 0, 0);
        isEmpty = true;
    }

    public Pt? player => trail.Count > 0 ? trail[trail.Count - 1].point : null;

    /// <summary>
    /// What a pixel's votes say: 0 unrevealed, 1 floor (two frames and a majority of the
    /// frames that saw it), 2 wall.
    /// </summary>
    public static byte state(int seen, int floor, int wall)
    {
        if (!(seen > 0)) return 0;
        return floor * 2 > seen ? (byte)(floor >= 2 ? 1 : 0) : (byte)2;
    }

    public readonly struct Placement
    {
        public readonly int dx; public readonly int dy; public readonly double score; public readonly bool placed;
        public Placement(int dx, int dy, double score, bool placed)
        {
            this.dx = dx; this.dy = dy; this.score = score; this.placed = placed;
        }
    }

    public Placement add(MinimapReading reading, double time)
    {
        if (isEmpty)
        {
            vote(reading);
            isEmpty = false;
            recordArrow(reading, time);
            return new Placement(0, 0, 1, true);
        }
        recentreIfNearEdge(reading);
        (double score, int dx, int dy) best = (-2.0, 0, 0);
        int r = searchRadius;
        // Coarse pass on a 3-pixel lattice, then a fine pass around the winner.
        for (int dy = -r; dy <= r; dy += 3)
        {
            for (int dx = -r; dx <= r; dx += 3)
            {
                double s = agreement(reading, dx, dy, 3);
                if (s > best.score) best = (s, dx, dy);
            }
        }
        var coarse = best;
        for (int dy = coarse.dy - 2; dy <= coarse.dy + 2; dy++)
        {
            for (int dx = coarse.dx - 2; dx <= coarse.dx + 2; dx++)
            {
                double s = agreement(reading, dx, dy, 2);
                if (s > best.score) best = (s, dx, dy);
            }
        }
        if (!(best.score >= minimumScore))
        {
            return new Placement(best.dx, best.dy, best.score, false);
        }
        int nx = offset.x + best.dx, ny = offset.y + best.dy;
        if (!(nx >= 0 && ny >= 0 && nx + reading.width < size && ny + reading.height < size))
        {
            return new Placement(best.dx, best.dy, best.score, false);
        }
        offset = (nx, ny);
        vote(reading);
        recordArrow(reading, time);
        return new Placement(best.dx, best.dy, best.score, true);
    }

    /// <summary>
    /// Keeps the map away from the canvas's edges by moving everything, so a dungeon larger
    /// than half the canvas in one direction is not mistaken for somewhere new (a placement
    /// off the canvas is refused, and 2.5 s of refusals starts a new map). Rare and cheap
    /// enough: two array copies when the reading comes within <c>edgeMargin</c> of an edge.
    /// </summary>
    internal const int edgeMargin = 400;

    private void recentreIfNearEdge(MinimapReading reading)
    {
        int m = edgeMargin;
        if (!(offset.x < m || offset.y < m
              || offset.x + reading.width > size - m || offset.y + reading.height > size - m)) return;
        int mapW = bounds.maxX - bounds.minX, mapH = bounds.maxY - bounds.minY;
        if (!(mapW < size - 2 * m && mapH < size - 2 * m)) return;   // nowhere to move it
        int dx = (size - mapW) / 2 - bounds.minX, dy = (size - mapH) / 2 - bounds.minY;
        var newFloor = new ushort[size * size];
        var newSeen = new ushort[size * size];
        var newWall = new ushort[size * size];
        for (int y = bounds.minY; y <= bounds.maxY; y++)
        {
            int from = y * size, to = (y + dy) * size + dx;
            for (int x = bounds.minX; x <= bounds.maxX; x++)
            {
                newFloor[to + x] = floorVotes[from + x];
                newSeen[to + x] = seenVotes[from + x];
                newWall[to + x] = wallVotes[from + x];
            }
        }
        floorVotes = newFloor;
        seenVotes = newSeen;
        wallVotes = newWall;
        offset = (offset.x + dx, offset.y + dy);
        bounds = (bounds.minX + dx, bounds.minY + dy, bounds.maxX + dx, bounds.maxY + dy);
        trail = trail.Select(s => (new Pt(s.point.x + (double)dx, s.point.y + (double)dy), s.time)).ToList();
        // Area stamps move by whole cells; the replay below re-stamps with the current area,
        // so keep the old grid and shift it rather than re-stamping.
        int n = visitColumns, cdx = dx / visitCell, cdy = dy / visitCell;
        var shifted = new byte[areaAt.Length];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                if (areaAt[y * n + x] == 0) continue;
                int nx = x + cdx, ny = y + cdy;
                if (nx >= 0 && ny >= 0 && nx < n && ny < n) shifted[ny * n + nx] = areaAt[y * n + x];
            }
        }
        int? keepArea = currentArea;
        currentArea = null;   // replay stamps nothing
        for (int i = 0; i < visited.Length; i++) visited[i] = false;
        lastStamp = null;
        foreach (var step in trail) stampVisit(step.point);
        currentArea = keepArea;
        areaAt = shifted;
        gateways = gateways.Select(g => (new Pt(g.point.x + (double)dx, g.point.y + (double)dy), g.areas)).ToList();
        _markMemory.shift(dx, dy);
        if (spot is Pt s) spot = new Pt(s.x + (double)dx, s.y + (double)dy);
        _landmarkMemory.shift(dx, dy);
        recentres += 1;
    }

    /// <summary>
    /// How many times the map has been moved on the canvas — navigation targets held in
    /// canvas coordinates across a move are stale, so holders compare this.
    /// </summary>
    public int recentres { get; private set; } = 0;

    /// <summary>
    /// Mean agreement (+1 same, −1 different) between the reading and the canvas's majority
    /// at this shift, over pixels both have seen. −2 when there is too little overlap.
    /// </summary>
    private double agreement(MinimapReading reading, int dx, int dy, int step)
    {
        int ox = offset.x + dx, oy = offset.y + dy;
        if (!(ox >= 0 && oy >= 0 && ox + reading.width < size && oy + reading.height < size)) return -2;
        int total = 0, count = 0, readable = 0;
        int y = 0;
        while (y < reading.height)
        {
            int x = 0;
            int row = (oy + y) * size + ox;
            while (x < reading.width)
            {
                int i = y * reading.width + x;
                if (!reading.ignore[i] && withinReach(reading, x, y))
                {
                    readable += 1;
                    int seen = (int)seenVotes[row + x];
                    if (seen > 0)
                    {
                        bool canvasFloor = (int)floorVotes[row + x] * 2 > seen;
                        total += (canvasFloor == reading.floor[i]) ? 1 : -1;
                        count += 1;
                    }
                }
                x += step;
            }
            y += step;
        }
        // Enough of what this reading can see must already be on the map. Measured against
        // the reading's own readable pixels, not the whole lattice: most dark pixels are
        // unseen now (see MinimapReader), and a fresh dungeon has little floor, so a share
        // of the lattice refused every frame after the first. And only the pixels within
        // revealRadius of the player count as readable — the same disc vote() records —
        // because pixels beyond it can never have been put on the map by this reading's
        // neighbours (29 Sep 2026: with the guide turned on mid-floor, every frame after a
        // reset scored −2 and the guide said "New map" every three seconds).
        if (!(readable > 0 && count * 5 >= readable * 2 && count >= 40)) return -2;
        return (double)total / (double)count;
    }

    /// Whether a minimap pixel is within revealRadius of the player's arrow — the part of a
    /// reading that vote() records. Without an arrow, everything counts.
    private static bool withinReach(MinimapReading reading, int x, int y)
    {
        if (reading.arrow is not Pt arrow) return true;
        double dx = x - arrow.x, dy = y - arrow.y;
        return dx * dx + dy * dy <= revealRadius * revealRadius;
    }

    private void vote(MinimapReading reading)
    {
        for (int y = 0; y < reading.height; y++)
        {
            int row = (offset.y + y) * size + offset.x;
            for (int x = 0; x < reading.width; x++)
            {
                int i = y * reading.width + x;
                if (reading.ignore[i]) continue;
                int k = row + x;
                // Nothing new appears beyond the reveal radius: the glow's speckled texture
                // counted as wall once floor was refused, and walled the Tomb off just the
                // same. A pixel never seen before votes only within reach of the player.
                if (seenVotes[k] == 0 && reading.arrow is Pt arrow
                    && M.Hypot((double)x - arrow.x, (double)y - arrow.y) > revealRadius) continue;
                if (!(seenVotes[k] < ushort.MaxValue - 1)) continue;
                seenVotes[k] += 1;
                if (reading.floor[i]) floorVotes[k] += 1; else wallVotes[k] += 1;
            }
        }
        bounds = (Math.Min(bounds.minX, offset.x), Math.Min(bounds.minY, offset.y),
                  Math.Max(bounds.maxX, offset.x + reading.width - 1), Math.Max(bounds.maxY, offset.y + reading.height - 1));
    }

    private void recordArrow(MinimapReading reading, double time)
    {
        if (reading.arrow is not Pt arrow) return;
        var point = new Pt((double)offset.x + arrow.x, (double)offset.y + arrow.y);
        trail.Add((point, time));
        stampVisit(point);
        // Marks go with a placed reading only: on a refused one the offset is unknown.
        double ox = (double)offset.x, oy = (double)offset.y;
        var view = new RectD(ox + 10, oy + 10, (double)reading.width - 32,
                             (double)reading.height * 0.86 - 10);
        _markMemory.update(reading.marks.Select(mk => new Pt(mk.x + ox, mk.y + oy)).ToArray(), view, time);
        newLandmarks = _landmarkMemory.update(reading.landmarks.Select(l => (l.Item1, new Pt(l.Item2.x + ox, l.Item2.y + oy))).ToArray());
    }

    /// <summary>
    /// The player stood still pushing along <c>facing</c> for seconds: whatever is 12–40 px
    /// ahead is not walkable, whatever the minimap says (walkway rails and water read as
    /// floor in the Ziggurat District, 27 Sep). Vote it wall, hard, so the route goes
    /// another way.
    /// </summary>
    public void markBlocked(double facing)
    {
        if (player is not Pt p) return;
        for (double d = 12.0; d <= 40.0; d += 4)
        {
            for (double side = -14.0; side <= 14.0; side += 4)
            {
                int x = (int)(p.x + Math.Sin(facing) * d + Math.Cos(facing) * side);
                int y = (int)(p.y - Math.Cos(facing) * d + Math.Sin(facing) * side);
                if (!(x >= 0 && y >= 0 && x < size && y < size)) continue;
                int i = y * size + x;
                seenVotes[i] = Math.Max(seenVotes[i], (ushort)40);
                wallVotes[i] = seenVotes[i];
                floorVotes[i] = 0;
            }
        }
    }

    /// <summary>
    /// Which way round an obstacle ahead: the bearing (perpendicular to <c>facing</c>) of
    /// the side with more floor 25–60 px out, or null when neither has any. Spoken as a
    /// compass word: "left" meant the character's left and the owner could not tell whose
    /// (27 Sep).
    /// </summary>
    public double? sidestep(double facing)
    {
        if (player is not Pt p) return null;
        int floorCount(double angle)
        {
            int n = 0;
            for (double d = 25.0; d <= 60.0; d += 7)
            {
                int x = (int)(p.x + Math.Sin(angle) * d), y = (int)(p.y - Math.Cos(angle) * d);
                if (!(x >= 0 && y >= 0 && x < size && y < size)) continue;
                int i = y * size + x;
                if (state((int)seenVotes[i], (int)floorVotes[i], (int)wallVotes[i]) == 1) n += 1;
            }
            return n;
        }
        int left = floorCount(facing - Math.PI / 2), right = floorCount(facing + Math.PI / 2);
        if (left == 0 && right == 0) return null;
        return left >= right ? facing - Math.PI / 2 : facing + Math.PI / 2;
    }

    /// <summary>
    /// Direction of travel over the last <c>window</c> seconds, radians clockwise from up,
    /// if the player moved far enough for it to mean anything.
    /// </summary>
    // PORT NOTE: the Swift signature is `heading(over window: Double = 0.6, minimumDistance:
    // Double = 8)`; the argument label `over` has no C# equivalent, so the parameter is
    // named `window`.
    public double? heading(double window = 0.6, double minimumDistance = 8)
    {
        if (trail.Count == 0) return null;
        var last = trail[trail.Count - 1];
        // trail.last(where:) — the last point at least `window` seconds before the last.
        (Pt point, double time)? earlier = null;
        for (int i = trail.Count - 1; i >= 0; i--)
        {
            if (last.time - trail[i].time >= window) { earlier = trail[i]; break; }
        }
        if (earlier == null) return null;
        var e = earlier.Value;
        double dx = last.point.x - e.point.x, dy = last.point.y - e.point.y;
        if (!(Math.Sqrt(dx * dx + dy * dy) >= minimumDistance)) return null;
        return M.Atan2(dx, -dy);
    }

    // image output omitted in the port (picture(path:target:region:) and the Picture enum)
}
