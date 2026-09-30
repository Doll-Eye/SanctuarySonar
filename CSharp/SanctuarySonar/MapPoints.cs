// The world map's points of interest, read off the map screen so a blind player can pick one
// from a list instead of sweeping the map with the cursor (the "zoom and sweep" every guide
// describes; asked for on Blizzard's forums in July 2025, unanswered).
//
// The map draws its icons as fixed sprites at a constant screen size whatever the zoom, in a
// handful of colours (measured on the 28 Sep 2026 07:12 recording, 2560×1608): waypoints are a
// cyan ring, towns a cyan shield, dungeons a white gate in a black outline (a green leaf on it
// when a Whisper is attached), strongholds a grey badge in a black outline with an orange flame,
// capstones a grey gate with a red padlock, Whisper bounties red skulls and swords, and the
// player a hollow white diamond. So: colour masks, connected blobs, a few size rules. The name of
// a point comes from the game's own tooltip once the pointer is on it (Shell.goToPoint).
using SanctuarySonar.Cartography;

namespace SanctuarySonar.Shell;

public sealed class MapPoint
{
    public string kind = "";
    public int x, y;             // client pixels
    public double bearing;       // from the player, radians, north = up
    public double distance;      // client pixels from the player
    public string? name;         // from the tooltip, once read
    public override string ToString() => $"{kind} at {x},{y}";
}

public static class MapIcons
{
    sealed class Blob
    {
        public int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, area;
        public int w => maxX - minX + 1;
        public int h => maxY - minY + 1;
        public int cx => (minX + maxX) / 2;
        public int cy => (minY + maxY) / 2;
    }

    /// <summary>Every icon found, nearest the player first. `player` is the white diamond if
    /// seen, else the picture's centre (the map opens centred on the player).</summary>
    public static (List<MapPoint> points, Pt player, bool diamondSeen) find(byte[] bgra, int width, int height)
    {
        double s = height / 1608.0;   // icon sizes scale with the picture
        int n = width * height;
        var cyan = new byte[n]; var red = new byte[n]; var green = new byte[n];
        var black = new byte[n]; var white = new byte[n]; var orange = new byte[n];
        for (int i = 0, p = 0; i < n; i++, p += 4)
        {
            int b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
            if (g >= 180 && b >= 180 && r <= 170 && g > r + 50 && b > r + 50) cyan[i] = 1;
            else if (r >= 180 && g <= 100 && b <= 120) red[i] = 1;
            else if (r >= 200 && g >= 120 && g <= 200 && b <= 80) orange[i] = 1;
            else if (g >= 190 && g > r + 40 && g > b + 20 && r <= 200) green[i] = 1;
            else if (r <= 50 && g <= 50 && b <= 50) black[i] = 1;
            else if (r >= 200 && g >= 200 && b >= 200) white[i] = 1;
        }

        int lo(double v) => (int)Math.Round(v * s);
        var points = new List<MapPoint>();
        var taken = new List<(int x, int y)>();
        bool near(int x, int y, int r) => taken.Any(t => Math.Abs(t.x - x) <= r && Math.Abs(t.y - y) <= r);
        // The map screen's own furniture, not the map: the tab bar and rank strip across the
        // top, the Whispers bar and buttons across the bottom, the side tabs at the right edge,
        // the event panel bottom-left. Measured at 2560×1608.
        double sx = width / 2560.0;
        bool inUI(int x, int y) => y < 200 * s || y > height - 200 * s || x > width - 200 * sx || (x < 480 * sx && y > 1000 * s);
        int count(byte[] mask, Blob b, int pad)
        {
            int c = 0;
            for (int y = Math.Max(0, b.minY - pad); y <= Math.Min(height - 1, b.maxY + pad); y++)
                for (int x = Math.Max(0, b.minX - pad); x <= Math.Min(width - 1, b.maxX + pad); x++)
                    c += mask[y * width + x];
            return c;
        }

        // Black-outlined icons: dungeons, strongholds, capstones, cleared things.
        foreach (var b in blobs(black, width, height, lo(250)))
        {
            if (b.w < lo(30) || b.w > lo(90) || b.h < lo(30) || b.h > lo(90)) continue;
            string kind;
            if (count(orange, b, lo(12)) >= lo(20)) kind = "Stronghold";
            else if (count(red, b, lo(12)) >= lo(20)) kind = "Capstone dungeon";
            else if (count(green, b, lo(14)) >= lo(20)) kind = "Dungeon with a Whisper";
            else if (count(white, b, 0) >= lo(50)) kind = "Dungeon";
            else kind = "Marker";
            points.Add(new MapPoint { kind = kind, x = b.cx, y = b.cy });
            taken.Add((b.cx, b.cy));
        }
        // Cyan: waypoints (a ring) and towns (a larger shield).
        foreach (var b in blobs(cyan, width, height, lo(120)))
        {
            if (b.w < lo(24) || b.w > lo(80) || b.h < lo(24) || b.h > lo(80)) continue;
            if (near(b.cx, b.cy, lo(30))) continue;
            points.Add(new MapPoint { kind = Math.Max(b.w, b.h) <= lo(48) ? "Waypoint" : "Town", x = b.cx, y = b.cy });
            taken.Add((b.cx, b.cy));
        }
        // Red on its own: Whisper bounties (the red padlock sits on a black-outlined capstone).
        foreach (var b in blobs(red, width, height, lo(50)))
        {
            if (b.w < lo(14) || b.w > lo(70) || b.h < lo(14) || b.h > lo(70)) continue;
            if (near(b.cx, b.cy, lo(34))) continue;
            points.Add(new MapPoint { kind = "Whisper", x = b.cx, y = b.cy });
            taken.Add((b.cx, b.cy));
        }
        // Green on its own: the green diamond icons (a leaf on a gate belongs to the dungeon).
        foreach (var b in blobs(green, width, height, lo(120)))
        {
            if (b.w < lo(24) || b.w > lo(80) || b.h < lo(24) || b.h > lo(80)) continue;
            if (near(b.cx, b.cy, lo(40))) continue;
            points.Add(new MapPoint { kind = "Green marker", x = b.cx, y = b.cy });
            taken.Add((b.cx, b.cy));
        }

        // Not the screen's furniture, and one point per icon (an outline broken in two gives
        // two blobs a pixel apart).
        var kept = new List<MapPoint>();
        foreach (var p in points)
        {
            if (inUI(p.x, p.y)) continue;
            if (kept.Any(k => Math.Abs(k.x - p.x) <= lo(22) && Math.Abs(k.y - p.y) <= lo(22))) continue;
            kept.Add(p);
        }
        points = kept;

        // The player: a hollow white diamond about 100 px across, near the middle (the map opens
        // centred on the player; panning moves it, so the nearest such blob to the centre wins).
        // The pointer arrow is smaller and solid, the map's white dots are tiny.
        Pt centre = new Pt(width / 2.0, height / 2.0);
        Pt player = centre;
        bool diamond = false;
        double best = double.PositiveInfinity;
        foreach (var b in blobs(white, width, height, lo(120)))
        {
            if (b.w < lo(40) || b.w > lo(130) || b.h < lo(40) || b.h > lo(130)) continue;
            if (inUI(b.cx, b.cy)) continue;
            double fill = b.area / (double)(b.w * b.h);
            if (fill > 0.45) continue;
            double d = Math.Abs(b.cx - centre.x) + Math.Abs(b.cy - centre.y);
            if (d > 0.4 * height || d >= best) continue;
            best = d; player = new Pt(b.cx, b.cy); diamond = true;
        }
        foreach (var p in points)
        {
            double dx = p.x - player.x, dy = p.y - player.y;
            p.distance = Math.Sqrt(dx * dx + dy * dy);
            p.bearing = Math.Atan2(dx, -dy);
        }
        points.Sort((a, b) => a.distance.CompareTo(b.distance));
        return (points, player, diamond);
    }

    /// <summary>8-connected components of a mask with at least `minArea` pixels.</summary>
    static List<Blob> blobs(byte[] mask, int width, int height, int minArea)
    {
        var seen = new byte[mask.Length];
        var result = new List<Blob>();
        var stack = new Stack<int>();
        for (int start = 0; start < mask.Length; start++)
        {
            if (mask[start] == 0 || seen[start] != 0) continue;
            var b = new Blob();
            seen[start] = 1; stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % width, y = i / width;
                b.area++;
                if (x < b.minX) b.minX = x; if (x > b.maxX) b.maxX = x;
                if (y < b.minY) b.minY = y; if (y > b.maxY) b.maxY = y;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy; if (yy < 0 || yy >= height) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx; if (xx < 0 || xx >= width) continue;
                        int j = yy * width + xx;
                        if (mask[j] != 0 && seen[j] == 0) { seen[j] = 1; stack.Push(j); }
                    }
                }
            }
            if (b.area >= minArea) result.Add(b);
        }
        return result;
    }

    /// <summary>"Waypoint, north-west, near." — what is said for a point.</summary>
    public static string describe(MapPoint p, int width)
    {
        double unit = width / 2560.0;
        string far = p.distance < 250 * unit ? "near" : p.distance < 650 * unit ? "a little way" : "far";
        string what = p.name ?? p.kind;
        return $"{what}, {Compass.word(p.bearing)}, {far}.";
    }
}
