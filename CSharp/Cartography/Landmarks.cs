// Port of Sources/Cartography/Landmarks.swift. Same names, same constants, same arithmetic.
namespace SanctuarySonar.Cartography;

/// <summary>
/// Icons on the minimap worth knowing about: what a sighted player would see appear and
/// what the full map only shows once found. The owner, 25 Sep 2026: "the dungeon map opens
/// empty until I find something like a healing well or barrier" — the minimap shows them
/// the moment they are near, so they are read from it.
///
/// Recognised by the shapes their bright pixels (≥ 100) make, measured on the owner's
/// Forbidden City recordings (446×320 minimap):
/// - Healing well: a heart, ~26×20 px, 260–290 bright pixels, with two flat strokes
///   (the bowl's rim, ~23×6 each) just below it. The boss room is usually near one.
/// - Arch: a keyhole-shaped arch, two shapes one inside the other, the outer ~22×24.
///   It looks like an area's entrance; one case — so the guide leads to an unvisited arch
///   only while the objective is to travel somewhere.
/// </summary>
public enum Landmark
{
    healingWell,
    arch,
}

/// <summary>The Swift enum's String raw values ("Healing well", "Arch").</summary>
public static class LandmarkExtensions
{
    // PORT NOTE: Swift's `rawValue` is a property; this is a C# 14 extension property so
    // `kind.rawValue` reads the same. If the compiler predates extension members, make it
    // an extension method.
    extension(Landmark kind)
    {
        public string rawValue => kind switch
        {
            Landmark.healingWell => "Healing well",
            Landmark.arch => "Arch",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>Swift's failable `Landmark(rawValue:)`.</summary>
    public static Landmark? fromRawValue(string rawValue) => rawValue switch
    {
        "Healing well" => Landmark.healingWell,
        "Arch" => Landmark.arch,
        _ => null,
    };
}

public static class LandmarkReader
{
    internal readonly struct Blob
    {
        public readonly int x; public readonly int y; public readonly int w; public readonly int h; public readonly int area;
        public Blob(int x, int y, int w, int h, int area) { this.x = x; this.y = y; this.w = w; this.h = h; this.area = area; }
        public double cx => (double)x + (double)w / 2;
        public double cy => (double)y + (double)h / 2;
    }

    internal const byte level = 100;

    /// <summary>
    /// Landmarks in minimap pixels. <c>exclude</c> is the arrow's position (its shapes are
    /// skipped); nothing in the bottom band (the quest marker) is considered.
    /// </summary>
    public static (Landmark, Pt)[] find(Gray gray, int bandTop, Pt? exclude)
    {
        Pt? arrow = exclude;
        int w = gray.width;
        var seen = new bool[w * gray.height];
        var blobs = new List<Blob>();
        for (int y = 0; y < bandTop; y++) for (int x = 0; x < w; x++)
        {
            if (!(gray[x, y] >= level && !seen[y * w + x])) continue;
            var stack = new Stack<(int, int)>(); stack.Push((x, y));
            int area = 0, minX = x, maxX = x, minY = y, maxY = y;
            seen[y * w + x] = true;
            while (stack.Count > 0)
            {
                var (cx, cy) = stack.Pop();
                area += 1;
                minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx); minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
                foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
                {
                    if (!(nx >= 0 && nx < w && ny >= 0 && ny < bandTop && !seen[ny * w + nx] && gray[nx, ny] >= level)) continue;
                    seen[ny * w + nx] = true;
                    stack.Push((nx, ny));
                }
            }
            if (!(area >= 25)) continue;
            var blob = new Blob(minX, minY, maxX - minX + 1, maxY - minY + 1, area);
            if (arrow is Pt a && M.Hypot(blob.cx - a.x, blob.cy - a.y) < 25) continue;
            blobs.Add(blob);
        }

        // The quest marker is a ring (~31×29, thinly filled) with a pin inside, and when its
        // target is in view it sits anywhere on the minimap, not only at the bottom edge —
        // its pin inside its ring read as an arch on 25 Sep. Anything within a ring goes.
        var rings = blobs.Where(b => b.w >= 26 && b.h >= 24 && (double)b.area / (double)(b.w * b.h) < 0.35).ToList();
        blobs.RemoveAll(b =>
            rings.Any(r =>
                b.cx > (double)(r.x - 6) && b.cx < (double)(r.x + r.w + 6) && b.cy > (double)(r.y - 6) && b.cy < (double)(r.y + r.h + 6)
            )
        );

        var found = new List<(Landmark, Pt)>();
        var used = new HashSet<int>();
        // Healing well: a heart with a flat stroke just below, overlapping it sideways.
        for (int i = 0; i < blobs.Count; i++)
        {
            var heart = blobs[i];
            if (!(heart.w >= 20 && heart.w <= 32 && heart.h >= 14 && heart.h <= 26 && heart.area >= 180 && heart.area <= 380)) continue;
            int? rim = null;
            for (int j = 0; j < blobs.Count; j++)
            {
                var b = blobs[j];
                if (j != i && b.h <= 10 && b.w >= 14 && b.y >= heart.y + heart.h - 2 && b.y <= heart.y + heart.h + 20
                    && b.x < heart.x + heart.w && b.x + b.w > heart.x) { rim = j; break; }
            }
            if (rim is int rimIndex)
            {
                used.Add(i); used.Add(rimIndex);
                found.Add((Landmark.healingWell, new Pt(heart.cx, heart.cy + 8)));
            }
        }
        // Arch: an outline with a second shape inside it.
        for (int i = 0; i < blobs.Count; i++)
        {
            var outer = blobs[i];
            if (!(!used.Contains(i) && outer.w >= 17 && outer.w <= 28 && outer.h >= 19 && outer.h <= 30)) continue;
            int? inner = null;
            for (int j = 0; j < blobs.Count; j++)
            {
                var b = blobs[j];
                if (j != i && !used.Contains(j) && b.w < outer.w && b.h <= outer.h
                    && b.cx > (double)outer.x && b.cx < (double)(outer.x + outer.w)
                    && b.cy > (double)outer.y && b.cy < (double)(outer.y + outer.h)) { inner = j; break; }
            }
            if (inner is int innerIndex)
            {
                used.Add(i); used.Add(innerIndex);
                found.Add((Landmark.arch, new Pt(outer.cx, outer.cy)));
            }
        }
        return found.ToArray();
    }
}

/// <summary>
/// Landmarks remembered on the stitched map for as long as the map lasts. Icons do not
/// move, so each is kept once, after <c>confirmations</c> sightings in the same place — one odd
/// frame must not invent a healing well. <c>update</c> returns the ones confirmed just now.
/// </summary>
// PORT NOTE: a Swift struct with mutating methods; a class here so that mutation through a
// property or a collection element is never lost on a copy.
public class LandmarkMemory
{
    public List<(Landmark kind, Pt point)> landmarks { get; private set; } = new();
    private List<(Landmark kind, Pt point, int count)> candidates = new();
    /// <summary>
    /// Within this many pixels, two sightings are one landmark. Wide for wells: the map
    /// drifts over a long run and one well was remembered four times, 90 px apart, on the
    /// 26 Sep Mariner's Refuge run ("Healing well east" said twice in one description).
    /// </summary>
    internal static double same(Landmark kind) => kind == Landmark.healingWell ? 120 : 50;
    internal const int confirmations = 3;

    public LandmarkMemory() { }
    public void clear() { landmarks = new(); candidates = new(); }

    public (Landmark, Pt)[] update((Landmark, Pt)[] current)
    {
        var @new = new List<(Landmark, Pt)>();
        foreach (var (kind, point) in current)
        {
            bool near(Pt p) => M.Hypot(p.x - point.x, p.y - point.y) < same(kind);
            if (landmarks.Any(l => l.kind == kind && near(l.point))) continue;
            int i = candidates.FindIndex(c => c.kind == kind && near(c.point));
            if (i >= 0)
            {
                var c = candidates[i];
                c.count += 1;
                candidates[i] = c;
                if (candidates[i].count >= confirmations)
                {
                    landmarks.Add((kind, point));
                    @new.Add((kind, point));
                    candidates.RemoveAt(i);
                }
            }
            else
            {
                candidates.Add((kind, point, 1));
            }
        }
        return @new.ToArray();
    }

    public void shift(int dx, int dy)
    {
        landmarks = landmarks.Select(l => (l.kind, new Pt(l.point.x + (double)dx, l.point.y + (double)dy))).ToList();
        candidates = candidates.Select(c => (c.kind, new Pt(c.point.x + (double)dx, c.point.y + (double)dy), c.count)).ToList();
    }
}
