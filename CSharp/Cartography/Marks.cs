// Port of Sources/Cartography/Marks.swift. Same names, same constants, same arithmetic.
namespace SanctuarySonar.Cartography;

/// <summary>
/// Red marks on the minimap: the red dots Diablo IV puts on every enemy a "slay all
/// enemies" objective still wants dead, and the red skull on an objective's target.
///
/// Read from the chroma plane (420v plane 1, Cb and Cr interleaved at half resolution).
/// Measured 25 Sep 2026 on the Ghastly Depths: the dots' Cr is 195–200 and Cb about 107,
/// against Cr ≈ 129 for everything else on the minimap; the quest marker's ring never
/// reached Cr 160.
/// </summary>
public static class RedMarks
{
    internal const byte minimumCr = 160;
    internal const byte maximumCb = 122;
    /// <summary>Chroma pixels in a blob (a dot is ~6 luma pixels across, so ~9 chroma pixels).</summary>
    internal const int minimumBlob = 3;

    /// <summary>
    /// Mark centres in minimap pixels. <c>lumaRect</c> is the minimap's rectangle in the luma
    /// plane; the chroma plane is half size in both directions.
    /// </summary>
    public static unsafe Pt[] find(byte* cbcr, int rowBytes, Rect lumaRect)
    {
        int cx0 = lumaRect.x / 2, cy0 = lumaRect.y / 2;
        int w = lumaRect.width / 2, h = lumaRect.height / 2;
        var red = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            byte* row = cbcr + (cy0 + y) * rowBytes + cx0 * 2;
            for (int x = 0; x < w; x++)
            {
                byte cb = row[x * 2], cr = row[x * 2 + 1];
                red[y * w + x] = cr >= minimumCr && cb <= maximumCb;
            }
        }
        var seen = new bool[w * h];
        var marks = new List<Pt>();
        for (int start = 0; start < w * h; start++)
        {
            if (!(red[start] && !seen[start])) continue;
            var stack = new Stack<int>(); stack.Push(start);
            int count = 0, sx = 0, sy = 0;
            seen[start] = true;
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                count += 1; sx += k % w; sy += k / w;
                int kx = k % w, ky = k / w;
                foreach (var (nx, ny) in new[] { (kx + 1, ky), (kx - 1, ky), (kx, ky + 1), (kx, ky - 1) })
                {
                    if (!(nx >= 0 && nx < w && ny >= 0 && ny < h)) continue;
                    int j = ny * w + nx;
                    if (red[j] && !seen[j]) { seen[j] = true; stack.Push(j); }
                }
            }
            if (!(count >= minimumBlob)) continue;
            // Back to luma (minimap) pixels: chroma pixel centres are at 2x + 0.5.
            marks.Add(new Pt(((double)sx / (double)count) * 2 + 1, ((double)sy / (double)count) * 2 + 1));
        }
        return marks.ToArray();
    }

    /// <summary>Safe form: the chroma plane starts at <paramref name="offset"/> in <paramref name="cbcr"/>.</summary>
    public static unsafe Pt[] find(byte[] cbcr, int offset, int rowBytes, Rect lumaRect)
    {
        fixed (byte* p = cbcr) { return find(p + offset, rowBytes, lumaRect); }
    }
}

/// <summary>
/// The objective marker: the bright cyan icon the game pins to the minimap's edge when the
/// objective is out of view — the Undercity's district boss on 27 Sep 2026, Y ≈ 188,
/// Cb ≈ 136, Cr ≈ 98. Nothing else on the minimap is bright and cyan: the healing well's
/// icon is neutral (Cb 124, Cr 129) and the attunement icons' blue is dim (Y ≈ 108).
/// </summary>
public static class ObjectiveMarker
{
    internal const byte minimumY = 150;
    internal const byte minimumCb = 128;
    internal const byte maximumCr = 112;
    internal const int minimumBlob = 6;
    // (A cap on blob size and a "not within 50 px of the player" rule were tried on 27 Sep
    // against a cyan false marker in the Forbidden City replay: neither removed it, and
    // together they took the 09:36 Undercity run's floor 2 from 9 / 4 to 15 / 9. Reverted.)

    /// <summary>
    /// The marker's centre in minimap-box pixels — it may lie outside the box — read from
    /// the captured <c>outer</c> rectangle, inside which the box sits at <c>inset</c>.
    /// </summary>
    public static unsafe Pt? find(byte* luma, int lumaRowBytes, byte* cbcr, int cbcrRowBytes,
                                  Rect outer, (int x, int y) inset)
    {
        var blobs = IconFinder.blobs(luma, lumaRowBytes, cbcr, cbcrRowBytes, outer, inset,
                                     minimumBlob, (y, cb, cr) => y >= minimumY && cb >= minimumCb && cr <= maximumCr);
        // Swift's max(by:) keeps the first of equal maxima; replace only on a strictly greater count.
        if (blobs.Count == 0) return null;
        var best = blobs[0];
        foreach (var b in blobs) { if (best.count < b.count) best = b; }
        return best.centre;
    }

    /// <summary>Safe form: each plane starts at its offset in its array (both may be the same NV12 buffer).</summary>
    public static unsafe Pt? find(byte[] luma, int lumaOffset, int lumaRowBytes, byte[] cbcr, int cbcrOffset, int cbcrRowBytes,
                                  Rect outer, (int x, int y) inset)
    {
        fixed (byte* lp = luma) fixed (byte* cp = cbcr)
        {
            return find(lp + lumaOffset, lumaRowBytes, cp + cbcrOffset, cbcrRowBytes, outer, inset);
        }
    }
}

/// <summary>
/// The Undercity's dim blue icons: braziers to ignite and attunement packs, both of which
/// buy time (measured 27 Sep 2026: Y ≈ 108, Cb ≈ 157, Cr ≈ 104; pinned to the box's edge
/// when out of view, like the marker). The healing well's icon is neutral.
/// </summary>
public static class BlueIcons
{
    internal const byte maximumY = 150;
    internal const byte minimumCb = 150;
    internal const byte maximumCr = 115;
    internal const int minimumBlob = 5;

    /// <summary>Centres in minimap-box pixels, largest first.</summary>
    public static unsafe Pt[] find(byte* luma, int lumaRowBytes, byte* cbcr, int cbcrRowBytes,
                                   Rect outer, (int x, int y) inset)
    {
        return IconFinder.blobs(luma, lumaRowBytes, cbcr, cbcrRowBytes, outer, inset,
                                minimumBlob, (y, cb, cr) => y < maximumY && cb >= minimumCb && cr <= maximumCr)
            .OrderByDescending(b => b.count).Select(b => b.centre).ToArray();
    }

    /// <summary>Safe form: each plane starts at its offset in its array (both may be the same NV12 buffer).</summary>
    public static unsafe Pt[] find(byte[] luma, int lumaOffset, int lumaRowBytes, byte[] cbcr, int cbcrOffset, int cbcrRowBytes,
                                   Rect outer, (int x, int y) inset)
    {
        fixed (byte* lp = luma) fixed (byte* cp = cbcr)
        {
            return find(lp + lumaOffset, lumaRowBytes, cp + cbcrOffset, cbcrRowBytes, outer, inset);
        }
    }
}

/// <summary>
/// The Undercity's orange icons: afflicted packs, each kill "+5 seconds" (measured 27 Sep
/// 2026 at Y ≈ 110, Cb ≈ 103, Cr ≈ 149 — under the red marks' Cr 160, over the floor's 130).
/// The time bonus of a timed run lives here: 112 → 194 s in four seconds on one pack.
/// </summary>
public static class OrangeIcons
{
    internal const byte minimumY = 80, maximumY = 210;
    internal const byte maximumCb = 115;
    internal const byte minimumCr = 140, maximumCr = 159;
    internal const int minimumBlob = 5;

    public static unsafe Pt[] find(byte* luma, int lumaRowBytes, byte* cbcr, int cbcrRowBytes,
                                   Rect outer, (int x, int y) inset)
    {
        return IconFinder.blobs(luma, lumaRowBytes, cbcr, cbcrRowBytes, outer, inset,
                                minimumBlob, (y, cb, cr) => y >= minimumY && y <= maximumY && cb <= maximumCb && cr >= minimumCr && cr <= maximumCr)
            .OrderByDescending(b => b.count).Select(b => b.centre).ToArray();
    }

    /// <summary>Safe form: each plane starts at its offset in its array (both may be the same NV12 buffer).</summary>
    public static unsafe Pt[] find(byte[] luma, int lumaOffset, int lumaRowBytes, byte[] cbcr, int cbcrOffset, int cbcrRowBytes,
                                   Rect outer, (int x, int y) inset)
    {
        fixed (byte* lp = luma) fixed (byte* cp = cbcr)
        {
            return find(lp + lumaOffset, lumaRowBytes, cp + cbcrOffset, cbcrRowBytes, outer, inset);
        }
    }
}

internal static class IconFinder
{
    /// <summary>Connected blobs of pixels passing <c>test</c> (luma, Cb, Cr), with centres in box pixels.</summary>
    internal static unsafe List<(Pt centre, int count)> blobs(byte* luma, int lumaRowBytes, byte* cbcr, int cbcrRowBytes,
                                                              Rect outer, (int x, int y) inset, int minimumBlob,
                                                              Func<byte, byte, byte, bool> test)
    {
        int cx0 = outer.x / 2, cy0 = outer.y / 2, w = outer.width / 2, h = outer.height / 2;
        var hit = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            byte* crow = cbcr + (cy0 + y) * cbcrRowBytes + cx0 * 2;
            byte* lrow = luma + (outer.y + y * 2) * lumaRowBytes + outer.x;
            for (int x = 0; x < w; x++) { if (test(lrow[x * 2], crow[x * 2], crow[x * 2 + 1])) hit[y * w + x] = true; }
        }
        var seen = new bool[w * h];
        var found = new List<(Pt centre, int count)>();
        for (int start = 0; start < w * h; start++)
        {
            if (!(hit[start] && !seen[start])) continue;
            var stack = new Stack<int>(); stack.Push(start);
            int count = 0, sx = 0, sy = 0;
            seen[start] = true;
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                count += 1; sx += k % w; sy += k / w;
                int kx = k % w, ky = k / w;
                foreach (var (nx, ny) in new[] { (kx + 1, ky), (kx - 1, ky), (kx, ky + 1), (kx, ky - 1) })
                {
                    if (!(nx >= 0 && nx < w && ny >= 0 && ny < h)) continue;
                    int j = ny * w + nx;
                    if (hit[j] && !seen[j]) { seen[j] = true; stack.Push(j); }
                }
            }
            if (count >= minimumBlob)
            {
                found.Add((new Pt((double)sx / (double)count * 2 + 1 - (double)inset.x,
                                  (double)sy / (double)count * 2 + 1 - (double)inset.y), count));
            }
        }
        return found;
    }
}

/// <summary>
/// Red marks remembered on the stitched map, so a target that has gone off the edge of
/// the minimap is still led to. A remembered mark is forgotten when the place it was is
/// in view again with no mark near it — the enemy is dead or has moved — or after
/// <c>forgetAfter</c> seconds unseen.
/// </summary>
// PORT NOTE: a Swift struct with mutating methods; a class here so that mutation through a
// property or a collection element is never lost on a copy.
public class MarkMemory
{
    public List<(Pt point, double seen)> marks { get; private set; } = new();
    internal const double merge = 18.0;
    /// <summary>
    /// Enemies move and die: on 26 Sep the guide carried 20–40 remembered marks and led to
    /// positions long empty ("wrong direction" at the end of the run). Half a minute.
    /// </summary>
    internal const double forgetAfter = 30.0;

    public MarkMemory() { }

    public void clear() { marks = new(); }

    /// <summary>
    /// <c>view</c> is the part of the canvas the reading covered, less its edges and the
    /// quest-marker band, where an absent mark really means absent.
    /// </summary>
    public void update(Pt[] current, RectD view, double time)
    {
        marks.RemoveAll(remembered =>
            time - remembered.seen > forgetAfter
                || (view.contains(remembered.point)
                    && !current.Any(c => M.Hypot(c.x - remembered.point.x, c.y - remembered.point.y) < merge * 1.5))
        );
        foreach (var point in current)
        {
            int i = marks.FindIndex(m => M.Hypot(m.point.x - point.x, m.point.y - point.y) < merge);
            if (i >= 0)
            {
                marks[i] = (point, time);
            }
            else
            {
                marks.Add((point, time));
            }
        }
    }

    public void shift(int dx, int dy)
    {
        marks = marks.Select(m => (new Pt(m.point.x + (double)dx, m.point.y + (double)dy), m.seen)).ToList();
    }
}
