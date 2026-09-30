// Port of Sources/Cartography/Minimap.swift. Same names, same constants, same arithmetic.
namespace SanctuarySonar.Cartography;

/// <summary>An 8-bit grey image, row-major.</summary>
public struct Gray
{
    public readonly int width;
    public readonly int height;
    public byte[] pixels;

    public Gray(int width, int height, byte[] pixels)
    {
        this.width = width; this.height = height; this.pixels = pixels;
    }

    /// <summary>
    /// Copies a rectangle out of a video-range luma plane (420v plane 0), stretching 16–235
    /// to 0–255 so levels match full-range stills.
    /// </summary>
    public unsafe Gray(byte* lumaPlane, int rowBytes, Rect rect)
    {
        width = rect.width; height = rect.height;
        var @out = new byte[rect.width * rect.height];
        for (int y = 0; y < rect.height; y++)
        {
            byte* row = lumaPlane + (rect.y + y) * rowBytes + rect.x;
            for (int x = 0; x < rect.width; x++)
            {
                @out[y * rect.width + x] = (byte)Math.Max(0, Math.Min(255, ((int)row[x] - 16) * 255 / 219));
            }
        }
        pixels = @out;
    }

    /// <summary>Safe form of the luma-plane constructor: the plane starts at <paramref name="offset"/> in <paramref name="lumaPlane"/>.</summary>
    public unsafe Gray(byte[] lumaPlane, int offset, int rowBytes, Rect rect)
    {
        fixed (byte* p = lumaPlane) { this = new Gray(p + offset, rowBytes, rect); }
    }

    public byte this[int x, int y] => pixels[y * width + x];

    /// <summary>
    /// The picture prepared for reading text: everything under <c>floor</c> black, the rest
    /// stretched. <b>Not used</b>: tried 26 Sep for the tracker over pale stone in Mariner's
    /// Refuge, and it dimmed the objective line (drawn in grey, not white) past reading.
    /// Rows above <c>fromRow</c> are left as they are: the area name above the minimap is set
    /// smaller and thinner, and the same treatment broke its strokes ("Hollow Ccivems").
    /// </summary>
    public Gray textEnhanced(int floor = 140, int fromRow = 0)
    {
        var @out = (byte[])pixels.Clone();
        double scale = 255.0 / (double)(255 - floor);
        for (int i = Math.Max(0, fromRow) * width; i < width * height; i++)
        {
            int v = (int)pixels[i] - floor;
            @out[i] = v <= 0 ? (byte)0 : (byte)Math.Min(255.0, (double)v * scale);
        }
        return new Gray(width, height, @out);
    }

    // image output omitted in the port

    /// <summary>
    /// Mean over a (2r+1)² box, by integral image. Kills the wall hatching and the floor's
    /// stone texture, which are finer than any corridor.
    /// </summary>
    public Gray boxBlur(int radius)
    {
        int r = radius;
        var integral = new int[(width + 1) * (height + 1)];
        for (int y = 0; y < height; y++)
        {
            int row = 0;
            for (int x = 0; x < width; x++)
            {
                row += (int)pixels[y * width + x];
                integral[(y + 1) * (width + 1) + x + 1] = integral[y * (width + 1) + x + 1] + row;
            }
        }
        var @out = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int y0 = Math.Max(0, y - r), y1 = Math.Min(height, y + r + 1);
            for (int x = 0; x < width; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(width, x + r + 1);
                int sum = integral[y1 * (width + 1) + x1] - integral[y0 * (width + 1) + x1]
                    - integral[y1 * (width + 1) + x0] + integral[y0 * (width + 1) + x0];
                @out[y * width + x] = (byte)(sum / ((x1 - x0) * (y1 - y0)));
            }
        }
        return new Gray(width, height, @out);
    }

    /// <summary>
    /// How busy the texture is round each pixel, in tenths of a grey level: the mean of
    /// |pixel − its 3×3 average| over a 7×7 box. Hatching is busy; smooth darkness and slow
    /// fades are not, because a gradient survives the 3×3 average.
    /// </summary>
    public Gray busyness()
    {
        var local = boxBlur(1);
        var detail = new byte[width * height];
        for (int i = 0; i < width * height; i++)
        {
            detail[i] = (byte)Math.Min(255, Math.Abs((int)pixels[i] - (int)local.pixels[i]) * 10);
        }
        return new Gray(width, height, detail).boxBlur(3);
    }

    /// <summary>
    /// Otsu's threshold over the pixels below <c>ceiling</c> (the arrow and icons are excluded so
    /// they do not pull the split between floor and void).
    /// </summary>
    public byte otsu(byte ceiling = 100)
    {
        var histogram = new int[256];
        foreach (byte p in pixels) { if (p < ceiling) histogram[(int)p] += 1; }
        int total = 0;
        foreach (int n in histogram) total += n;
        if (!(total > 0)) return 30;
        int sumAll = 0;
        for (int i = 0; i < histogram.Length; i++) sumAll += i * histogram[i];
        int sumB = 0, weightB = 0, threshold = 0;
        double best = 0.0;
        for (int t = 0; t < 256; t++)
        {
            weightB += histogram[t];
            if (weightB == 0) continue;
            int weightF = total - weightB;
            if (weightF == 0) break;
            sumB += t * histogram[t];
            double meanB = (double)sumB / (double)weightB;
            double meanF = (double)(sumAll - sumB) / (double)weightF;
            double between = (double)weightB * (double)weightF * (meanB - meanF) * (meanB - meanF);
            if (between > best) { best = between; threshold = t; }
        }
        return (byte)threshold;
    }
}

/// <summary>
/// Where the minimap is inside the game picture.
///
/// Measured once, on the 25 Sep 2026 Shadow recording: a 2646×1720 window with a 57-pixel
/// title bar, so a 2646×1663 game picture, minimap at 2166–2611 × 135–455 of the window.
/// Expressed against the picture's height and anchored to its top-right corner, on the
/// assumption that Diablo IV scales its HUD with the picture's height. <b>Unverified</b> for
/// other sizes, full screen, and GeForce NOW / PS Remote Play; <c>MinimapReading.isLegible</c>
/// is how a wrong guess shows up.
/// </summary>
public static class MinimapLayout
{
    internal const double referenceHeight = 1663.0;
    internal const double rightInset = 35.0 / referenceHeight;
    internal const double width = 446.0 / referenceHeight;
    internal const double top = 78.0 / referenceHeight;
    internal const double height = 320.0 / referenceHeight;

    /// <summary>
    /// The minimap's rectangle in window pixels, given the window's pixel size and how much
    /// of its top is title bar rather than game.
    /// </summary>
    public static Rect rect(int windowWidth, int windowHeight, int titleBar)
    {
        double pictureHeight = (double)(windowHeight - titleBar);
        int w = (int)(width * pictureHeight), h = (int)(height * pictureHeight);
        int x = windowWidth - (int)(rightInset * pictureHeight) - w;
        int y = titleBar + (int)(top * pictureHeight);
        return new Rect(Math.Max(0, x), Math.Max(0, y), Math.Min(w, windowWidth), Math.Min(h, windowHeight));
    }

    /// <summary>
    /// Icons pinned to the minimap's edge — the objective marker in the Undercity, 27 Sep
    /// 2026 — straddle the border, half outside the box, so the capture takes a margin
    /// round it. The reader still sees only the box.
    /// </summary>
    public const int margin = 30;

    /// <summary>
    /// The box grown by <c>margin</c> on every side, clamped to the window, and where the box
    /// sits inside that.
    /// </summary>
    public static (Rect rect, (int x, int y) inset) outer(Rect box, int windowWidth, int windowHeight)
    {
        // Even edges keep the chroma plane (half size) aligned with the luma plane.
        int x = Math.Max(0, box.x - margin) & ~1, y = Math.Max(0, box.y - margin) & ~1;
        int right = Math.Min(windowWidth, box.x + box.width + margin), bottom = Math.Min(windowHeight, box.y + box.height + margin);
        return (new Rect(x, y, right - x, bottom - y), (box.x - x, box.y - y));
    }
}

/// <summary>What one frame's minimap says.</summary>
public struct MinimapReading
{
    /// <summary>true where the blurred minimap is floor.</summary>
    public bool[] floor;
    /// <summary>true where the reading should not be trusted: the arrow, icons, the border.</summary>
    public bool[] ignore;
    /// <summary>
    /// true where the frame shows smooth darkness: not floor, not wall, just not revealed
    /// yet. Ignored for stitching, but it votes on the map — see <c>Stitcher.vote</c>.
    /// </summary>
    public bool[] unseen;
    public readonly int width;
    public readonly int height;
    /// <summary>The player's arrow, in minimap pixels, if found.</summary>
    public Pt? arrow;
    /// <summary>Which way the arrow points — the character's facing — radians clockwise from up.</summary>
    public double? facing;
    public readonly byte threshold;
    /// <summary>Mean brightness of floor and of void; their gap is how legible this frame is.</summary>
    public readonly double floorLevel;
    public readonly double voidLevel;
    public readonly double floorFraction;
    /// <summary>
    /// Red marks (objective targets) in minimap pixels, found from the chroma plane by the
    /// caller — the reader itself sees only brightness.
    /// </summary>
    public Pt[] marks;
    /// <summary>Healing wells and arches in view, in minimap pixels.</summary>
    public (Landmark, Pt)[] landmarks;

    /// <summary>
    /// Share of the minimap with busy texture. The minimap measured 0.13–0.17 on 26 Sep, and
    /// up to 0.31 in a tangle of hatched corridors; the inventory screen over it 0.38 —
    /// inventory frames that got past the other tests stitched blocks of junk into the map.
    /// </summary>
    public double busyFraction;
    /// <summary>
    /// Above this share of busy pixels the frame is a menu (the inventory), not the map.
    /// 0.34 live; a recording's compression adds texture, and the Undercity's fog sat at
    /// 0.35–0.39 on the 27 Sep replay while the live guide read it — MapLab may raise it.
    /// </summary>
    // 0.60 since 29 Sep 2026: native rendering reads 0.45–0.48 on some districts (see the Swift note).
    public static double busyLimit = 0.60;

    /// <summary>The Swift memberwise initialiser; the defaulted members follow the required ones.</summary>
    public MinimapReading(bool[] floor, bool[] ignore, int width, int height, Pt? arrow, byte threshold,
                          double floorLevel, double voidLevel, double floorFraction,
                          bool[]? unseen = null, double? facing = null, Pt[]? marks = null,
                          (Landmark, Pt)[]? landmarks = null, double busyFraction = 0)
    {
        this.floor = floor; this.ignore = ignore; this.unseen = unseen ?? Array.Empty<bool>();
        this.width = width; this.height = height; this.arrow = arrow; this.facing = facing;
        this.threshold = threshold; this.floorLevel = floorLevel; this.voidLevel = voidLevel;
        this.floorFraction = floorFraction;
        this.marks = marks ?? Array.Empty<Pt>();
        this.landmarks = landmarks ?? Array.Empty<(Landmark, Pt)>();
        this.busyFraction = busyFraction;
    }

    public double contrast => floorLevel - voidLevel;

    /// <summary>
    /// Whether this looks like a minimap at all. A menu, the full map, a loading screen or a
    /// wrongly placed rectangle fails at least one of these. From one recording: contrast
    /// ≈ 16 and floor 20–60 % throughout play.
    /// </summary>
    public bool isLegible =>
        arrow != null && contrast >= 8 && floorFraction > 0.05 && floorFraction < 0.9 && busyFraction < busyLimit;
}

public static class MinimapReader
{
    /// <summary>Pixels this bright are the arrow, footprints, icons — never floor or void.</summary>
    internal const byte brightLimit = 100;
    /// <summary>
    /// The bottom band is where the edge-clamped quest marker lives. The arrow is never
    /// looked for there, but the band <i>is</i> read as map: ignoring all of it (the first
    /// version) made the guide see less far south than any other way — it led the owner into
    /// a dead end on 25 Sep. A disc round the marker is ignored and then filled in from its
    /// surroundings.
    /// </summary>
    internal const double bottomBand = 0.14;
    internal const double markerRadius = 0.12;   // of the minimap's height
    /// <summary>The frame's decoration; the right side's is wider and read as map when not excluded.</summary>
    internal const int border = 8;
    internal const int rightBorder = 20;
    /// <summary>How far to look for readable map when filling in under an icon.</summary>
    internal const int fillReach = 45;
    /// <summary>Below this busyness (tenths), dark is unseen rather than wall. See <c>read</c>.</summary>
    internal const byte smoothLimit = 18;
    /// <summary>A drop in light this big across five pixels of a floor edge makes it a wall.</summary>
    internal const int sharpDrop = 10;
    /// <summary>Floor patches smaller than this many pixels are specks, not floor.</summary>
    internal const int minimumFloorPatch = 150;

    /// <summary>The four neighbours, in the Swift source's order: right, left, down, up.</summary>
    private static readonly (int dx, int dy)[] fourWays = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    public static MinimapReading read(Gray gray)
    {
        int w = gray.width, h = gray.height;
        var blurred = gray.boxBlur(4);
        byte threshold = Math.Max(blurred.otsu(brightLimit), (byte)24);

        var ignore = new bool[w * h];
        // Bright things, grown by a few pixels so their halo does not read as floor.
        int grow = 5;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            if (!(gray[x, y] >= brightLimit)) continue;
            for (int yy = Math.Max(0, y - grow); yy < Math.Min(h, y + grow + 1); yy++)
            {
                for (int xx = Math.Max(0, x - grow); xx < Math.Min(w, x + grow + 1); xx++) ignore[yy * w + xx] = true;
            }
        }
        int bandTop = (int)((double)h * (1 - bottomBand));
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            if (x < border || x >= w - rightBorder || y < border || y >= h - border)
                ignore[y * w + x] = true;
        }
        // The quest marker: the bright pixels in the bottom band. Its dark gold parts are
        // not bright, so the whole disc round it goes, not just the bright pixels.
        int mx = 0, my = 0, mcount = 0;
        for (int y = bandTop; y < h; y++) for (int x = 0; x < w; x++)
        {
            if (gray[x, y] >= brightLimit) { mx += x; my += y; mcount += 1; }
        }
        if (mcount >= 20)
        {
            int cx = mx / mcount, cy = my / mcount, r = (int)(markerRadius * (double)h);
            for (int y = Math.Max(0, cy - r); y < Math.Min(h, cy + r + 1); y++) for (int x = Math.Max(0, cx - r); x < Math.Min(w, cx + r + 1); x++)
            {
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r)
                    ignore[y * w + x] = true;
            }
        }

        var floor = new bool[w * h];
        double floorSum = 0.0, floorCount = 0.0, voidSum = 0.0, voidCount = 0.0;
        for (int i = 0; i < w * h; i++)
        {
            if (ignore[i]) continue;
            byte v = blurred.pixels[i];
            if (v > threshold)
            {
                floor[i] = true; floorSum += (double)v; floorCount += 1;
            }
            else
            {
                voidSum += (double)v; voidCount += 1;
            }
        }
        double counted = floorCount + voidCount;

        // Fill in under icons from what surrounds them. Left ignored, every icon — the
        // arrow, the quest marker, a healing well, a door — was a patch of map never seen,
        // and a never-seen patch beside floor is an "unexplored opening": on 25 Sep the
        // guide swung between a healing well, a door, and the quest marker's patch (always
        // ~130 px south of the player) for five minutes. Each ignored pixel takes the
        // majority of the first readable pixel in each of the four directions, within
        // `fillReach`. The frame's own border stays ignored: beyond it really is unseen.
        bool isBorder(int x, int y) =>
            x < border || x >= w - rightBorder || y < border || y >= h - border;
        var filledFloor = (bool[])floor.Clone();
        var filledIgnore = (bool[])ignore.Clone();
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            if (!(ignore[y * w + x] && !isBorder(x, y))) continue;
            int votes = 0, floors = 0;
            foreach (var (dx, dy) in fourWays)
            {
                int step = 1;
                while (step <= fillReach)
                {
                    int nx = x + dx * step, ny = y + dy * step;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h || isBorder(nx, ny)) break;
                    int j = ny * w + nx;
                    if (!ignore[j]) { votes += 1; if (floor[j]) floors += 1; break; }
                    step += 1;
                }
            }
            if (!(votes >= 2)) continue;
            filledFloor[y * w + x] = floors * 2 >= votes;
            filledIgnore[y * w + x] = false;
        }
        floor = filledFloor;
        ignore = filledIgnore;

        // Smooth darkness is unseen, not empty. The minimap draws only floor the player has
        // been near: unexplored floor is as dark as solid rock, and the edge of the fog is
        // a soft fade, while a real wall is a light outline with diagonal hatching beyond
        // it. Read as "seen, not floor", every dark pixel closed the explored area in on
        // all sides, and after the owner restarted the dungeon on 25 Sep the guide found no
        // openings for eighteen minutes. So dark pixels count as wall only where the texture
        // is busy (hatching); smooth dark ones are left unseen, and floor that fades into
        // them is an opening. Measured on that run: fade and far darkness 0.3–1.5, hatching
        // 2.2–3.3 (mean absolute difference from a 3×3 average, over 7×7).
        var busy = gray.busyness();
        int busyCount = 0;
        for (int i = 0; i < w * h; i++) { if (busy.pixels[i] >= smoothLimit) busyCount += 1; }
        var unseen = new bool[w * h];
        for (int i = 0; i < w * h; i++)
        {
            if (!(!floor[i] && !ignore[i] && busy.pixels[i] < smoothLimit)) continue;
            ignore[i] = true;
            unseen[i] = true;
        }

        // A sharp edge is a wall even without hatching. Ledges and drops are drawn as a
        // plain edge — the corridor the owner walked on 25 Sep had one along its whole
        // south side, and with smooth dark counted as unseen it read as an endless opening.
        // What sets the fog apart is the fade: 40 → 25 over about twelve pixels, against
        // 40 → 26 within three at a ledge. So where floor meets unseen dark, compare the
        // light a pixel inside the edge with the light four pixels out; a big drop seals
        // the edge with a thin strip of wall, a gentle one leaves it open.
        var fine = gray.boxBlur(1);
        var @sealed = new List<int>();
        // PORT NOTE: Swift's `1..<(h - 1)` traps when h < 2; these loops simply run zero times.
        for (int y = 1; y < h - 1; y++) for (int x = 1; x < w - 1; x++)
        {
            if (!floor[y * w + x]) continue;
            foreach (var (dx, dy) in fourWays)
            {
                int q = (y + dy) * w + (x + dx);
                if (!(!floor[q] && ignore[q] && !isBorder(x + dx, y + dy))) continue;
                int ix = x - dx, iy = y - dy, ox = x + 4 * dx, oy = y + 4 * dy;
                if (!(ix >= 0 && iy >= 0 && ix < w && iy < h && ox >= 0 && oy >= 0 && ox < w && oy < h)) continue;
                int drop = (int)fine[ix, iy] - (int)fine[ox, oy];
                if (drop >= sharpDrop)
                {
                    for (int step = 1; step <= 3; step++)
                    {
                        int sx = x + dx * step, sy = y + dy * step;
                        if (sx >= 0 && sy >= 0 && sx < w && sy < h && !floor[sy * w + sx]) @sealed.Add(sy * w + sx);
                    }
                }
            }
        }
        foreach (int k in @sealed) { if (!isBorder(k % w, k / w)) { ignore[k] = false; unseen[k] = false; } }

        // Specks: patches of "floor" too small to be any real floor — text or effects
        // flashing over the minimap. They became islands of floor in the dark on the
        // 25 Sep map, each one an "opening". Dropped, and left unseen.
        var label = new int[w * h];
        Array.Fill(label, -1);
        for (int start = 0; start < w * h; start++)
        {
            if (!(floor[start] && label[start] < 0)) continue;
            var stack = new Stack<int>(); stack.Push(start);
            var members = new List<int>();
            label[start] = start;
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                members.Add(k);
                int kx = k % w, ky = k / w;
                foreach (var (nx, ny) in new[] { (kx + 1, ky), (kx - 1, ky), (kx, ky + 1), (kx, ky - 1) })
                {
                    if (!(nx >= 0 && nx < w && ny >= 0 && ny < h)) continue;
                    int j = ny * w + nx;
                    if (floor[j] && label[j] < 0) { label[j] = start; stack.Push(j); }
                }
            }
            if (members.Count < minimumFloorPatch)
            {
                foreach (int k in members) { floor[k] = false; ignore[k] = true; }
            }
        }

        Pt? arrow = findArrow(gray, bandTop);
        var reading = new MinimapReading(floor: floor, ignore: ignore, width: w, height: h,
                                         arrow: arrow,
                                         threshold: threshold,
                                         floorLevel: floorCount > 0 ? floorSum / floorCount : 0,
                                         voidLevel: voidCount > 0 ? voidSum / voidCount : 0,
                                         floorFraction: counted > 0 ? floorCount / counted : 0);
        reading.busyFraction = (double)busyCount / (double)(w * h);
        reading.unseen = unseen;
        reading.landmarks = LandmarkReader.find(gray, bandTop, arrow);
        if (arrow is Pt a) reading.facing = arrowFacing(gray, a);
        return reading;
    }

    /// <summary>
    /// Measured on the 25 Sep recording: the arrow peaks around 145 after the video-range
    /// stretch, about 220 pixels of it at 100 or more; footprints are a few pixels each.
    /// </summary>
    internal const byte arrowLevel = 100;

    /// <summary>
    /// The arrow is the bright cluster nearest the middle of the minimap, where the game
    /// keeps the player (measured x 209–236, y 147–173 of 446×320). The first version took
    /// the <i>largest</i> bright cluster, and a healing-well icon is larger and brighter than the
    /// arrow: on 25 Sep the player's position kept jumping to the well.
    /// </summary>
    internal const double arrowSearchRadius = 0.25;   // of the minimap's height, from its centre

    /// <summary>
    /// The arrow's facing. The bright pixel farthest from the arrow's centre is its <i>tail</i>
    /// (measured: that direction was opposite to travel 77 % of the time on the 22-minute
    /// run), so the facing is from that pixel through the centre.
    /// </summary>
    public static double? arrowFacing(Gray gray, Pt arrow)
    {
        int r = 18;
        var pts = new List<(double, double)>();
        for (int y = Math.Max(0, (int)arrow.y - r); y < Math.Min(gray.height, (int)arrow.y + r); y++)
        {
            for (int x = Math.Max(0, (int)arrow.x - r); x < Math.Min(gray.width, (int)arrow.x + r); x++)
            {
                if (gray[x, y] >= arrowLevel) pts.Add(((double)x, (double)y));
            }
        }
        if (!(pts.Count >= 40)) return null;
        double sumX = 0, sumY = 0;
        foreach (var p in pts) { sumX += p.Item1; sumY += p.Item2; }
        double cx = sumX / (double)pts.Count, cy = sumY / (double)pts.Count;
        // Swift's max(by:) keeps the first of equal maxima; replace only on a strictly greater distance.
        (double, double) tip = pts[0];
        foreach (var p in pts)
        {
            if (M.Hypot(tip.Item1 - cx, tip.Item2 - cy) < M.Hypot(p.Item1 - cx, p.Item2 - cy)) tip = p;
        }
        if (!(M.Hypot(tip.Item1 - cx, tip.Item2 - cy) >= 5)) return null;
        return M.Atan2(cx - tip.Item1, -(cy - tip.Item2));
    }

    /// <summary>The arrow as drawn while the player stands in a floor's starting bubble is grey, not
    /// white (30 Sep 2026): a second pass takes a dimmer, smaller cluster near the centre.</summary>
    internal const byte dimArrowLevel = 80;
    internal const int dimArrowMinimum = 12;
    internal const double dimArrowReach = 40.0;

    internal static Pt? findArrow(Gray gray, int bandTop)
    {
        return findArrow(gray, bandTop, arrowLevel, 60, arrowSearchRadius * (double)gray.height)
            ?? findArrow(gray, bandTop, dimArrowLevel, dimArrowMinimum, dimArrowReach);
    }

    private static Pt? findArrow(Gray gray, int bandTop, byte level, int minimum, double reach)
    {
        int w = gray.width;
        var centre = (x: (double)w / 2, y: (double)gray.height / 2);
        var seen = new bool[w * gray.height];
        (int count, int sx, int sy, double gap) best = (0, 0, 0, double.PositiveInfinity);
        for (int y = 0; y < bandTop; y++) for (int x = 0; x < w; x++)
        {
            if (!(gray[x, y] >= level && !seen[y * w + x])) continue;
            var stack = new Stack<(int, int)>(); stack.Push((x, y));
            int count = 0, sx = 0, sy = 0;
            seen[y * w + x] = true;
            while (stack.Count > 0)
            {
                var (cx, cy) = stack.Pop();
                count += 1; sx += cx; sy += cy;
                foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
                {
                    if (!(nx >= 0 && nx < w && ny >= 0 && ny < bandTop && !seen[ny * w + nx] && gray[nx, ny] >= level)) continue;
                    seen[ny * w + nx] = true;
                    stack.Push((nx, ny));
                }
            }
            if (!(count >= minimum)) continue;
            double gap = M.Hypot((double)sx / (double)count - centre.x, (double)sy / (double)count - centre.y);
            if (gap < reach && gap < best.gap) best = (count, sx, sy, gap);
        }
        if (!(best.count >= minimum)) return null;
        return new Pt((double)best.sx / (double)best.count, (double)best.sy / (double)best.count);
    }
}
