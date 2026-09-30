// Window capture by BitBlt from the screen: the game's client area, as composed on the
// desktop, which is what a windowed or borderless-fullscreen game shows. Exclusive
// fullscreen would need Windows Graphics Capture instead; not done yet.
using System.Runtime.InteropServices;
using System.Text;

namespace SanctuarySonar.Shell;

public sealed class GameWindow
{
    public nint handle;
    public string title = "";
    public int clientLeft, clientTop, clientWidth, clientHeight;

    public override string ToString() => $"{title} ({clientWidth} by {clientHeight} at {clientLeft},{clientTop})";

    /// <summary>All visible top-level windows with a title.</summary>
    public static List<GameWindow> list()
    {
        var found = new List<GameWindow>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            Native.GetWindowTextW(h, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.Length == 0) return true;
            var w = new GameWindow { handle = h, title = title };
            if (w.refresh() && w.clientWidth > 200 && w.clientHeight > 200) found.Add(w);
            return true;
        }, 0);
        return found;
    }

    public static GameWindow? find(string titlePart) =>
        list().FirstOrDefault(w => w.title.Contains(titlePart, StringComparison.OrdinalIgnoreCase));

    /// <summary>Re-reads the client rectangle in screen pixels. False if the window has gone.</summary>
    public bool refresh()
    {
        if (!Native.IsWindow(handle)) return false;
        if (!Native.GetClientRect(handle, out var r)) return false;
        var origin = new Native.POINT { x = 0, y = 0 };
        Native.ClientToScreen(handle, ref origin);
        clientLeft = origin.x; clientTop = origin.y;
        clientWidth = r.right - r.left; clientHeight = r.bottom - r.top;
        return clientWidth > 0 && clientHeight > 0;
    }
}

/// <summary>Grabs rectangles of the screen into BGRA, and converts to NV12 for the reader.</summary>
public sealed class ScreenGrabber : IDisposable
{
    nint memDC, bitmap, oldBitmap, bits;
    int bitmapWidth, bitmapHeight;
    readonly bool bottomUp;

    /// <summary>`bottomUp`: rows from the bottom of the picture up, as Media Foundation reads a
    /// BGRA buffer (the recorder); the default is top-down, as the reader expects.</summary>
    public ScreenGrabber(bool bottomUp = false) { this.bottomUp = bottomUp; }

    // The game window's own picture, rendered once per frame by PrintWindow (DWM's copy of the
    // window, so another window on top — Sanctuary Sonar's own, on 28 Sep — does not get into
    // the minimap). `grab` then copies rectangles out of it. Without a window frame, or if
    // PrintWindow fails, `grab` reads the screen as before.
    nint winDC, winBitmap, winOldBitmap, winBits;
    int winWidth, winHeight, winLeft, winTop;
    bool haveWindowFrame;
    /// <summary>"window" or "screen": which source the last frame came from.</summary>
    public string source { get; private set; } = "screen";

    /// <summary>Renders the window's client area for this frame. Call once per frame before
    /// the grabs; false (and screen capture) if the window cannot be rendered.</summary>
    public bool beginFrame(GameWindow window)
    {
        haveWindowFrame = false;
        int w = window.clientWidth, h = window.clientHeight;
        if (w <= 0 || h <= 0) return false;
        if (winBitmap == 0 || w != winWidth || h != winHeight)
        {
            releaseWindowFrame();
            var screen = Native.GetDC(0);
            winDC = Native.CreateCompatibleDC(screen);
            var info = new Native.BITMAPINFO();
            info.bmiHeader.biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>();
            info.bmiHeader.biWidth = w; info.bmiHeader.biHeight = -h; info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = 32;
            winBitmap = Native.CreateDIBSection(winDC, ref info, 0, out winBits, 0, 0);
            winOldBitmap = Native.SelectObject(winDC, winBitmap);
            winWidth = w; winHeight = h;
            Native.ReleaseDC(0, screen);
        }
        winLeft = window.clientLeft; winTop = window.clientTop;
        haveWindowFrame = Native.PrintWindow(window.handle, winDC, Native.PW_CLIENTONLY | Native.PW_RENDERFULLCONTENT);
        source = haveWindowFrame ? "window" : "screen";
        return haveWindowFrame;
    }

    /// <summary>Forget the window frame: `grab` reads the screen until the next `beginFrame`.</summary>
    public void endFrame() => haveWindowFrame = false;

    /// <summary>BGRA pixels of the screen rectangle (screen coordinates), row stride = width * 4;
    /// from the window's rendered frame when `beginFrame` succeeded this frame.</summary>
    public unsafe byte[]? grab(int left, int top, int width, int height, int scaleDivisor = 1)
    {
        int outW = width / scaleDivisor, outH = height / scaleDivisor;
        if (outW <= 0 || outH <= 0) return null;
        var screen = Native.GetDC(0);
        if (screen == 0) return null;
        try
        {
            ensureBitmap(screen, outW, outH);
            nint src = haveWindowFrame ? winDC : screen;
            int sx = haveWindowFrame ? left - winLeft : left, sy = haveWindowFrame ? top - winTop : top;
            int rop = haveWindowFrame ? Native.SRCCOPY : Native.SRCCOPY | Native.CAPTUREBLT;
            if (scaleDivisor == 1)
            {
                if (!Native.BitBlt(memDC, 0, 0, outW, outH, src, sx, sy, rop)) return null;
            }
            else
            {
                Native.SetStretchBltMode(memDC, Native.HALFTONE);
                if (!Native.StretchBlt(memDC, 0, 0, outW, outH, src, sx, sy, width, height, rop)) return null;
            }
            Native.GdiFlush();
            var result = new byte[outW * outH * 4];
            Marshal.Copy(bits, result, 0, result.Length);
            return result;
        }
        finally { Native.ReleaseDC(0, screen); }
    }

    void releaseWindowFrame()
    {
        if (winDC != 0) { if (winOldBitmap != 0) Native.SelectObject(winDC, winOldBitmap); Native.DeleteDC(winDC); winDC = 0; }
        if (winBitmap != 0) { Native.DeleteObject(winBitmap); winBitmap = 0; }
        haveWindowFrame = false;
    }

    void ensureBitmap(nint screen, int w, int h)
    {
        if (bitmap != 0 && w == bitmapWidth && h == bitmapHeight) return;
        release();
        memDC = Native.CreateCompatibleDC(screen);
        var info = new Native.BITMAPINFO();
        info.bmiHeader.biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>();
        info.bmiHeader.biWidth = w;
        info.bmiHeader.biHeight = bottomUp ? h : -h;   // negative = top-down
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = 0;
        bitmap = Native.CreateDIBSection(memDC, ref info, 0, out bits, 0, 0);
        oldBitmap = Native.SelectObject(memDC, bitmap);
        bitmapWidth = w; bitmapHeight = h;
    }

    void release()
    {
        if (memDC != 0) { if (oldBitmap != 0) Native.SelectObject(memDC, oldBitmap); Native.DeleteDC(memDC); memDC = 0; }
        if (bitmap != 0) { Native.DeleteObject(bitmap); bitmap = 0; }
    }

    public void Dispose() { release(); releaseWindowFrame(); }

    /// <summary>BGRA → NV12 (BT.601 video range, the same numbers Apple's 420v carries): a luma
    /// plane, then Cb/Cr interleaved at half size, each chroma sample the mean of its 2×2 block.</summary>
    public static byte[] toNV12(byte[] bgra, int width, int height)
    {
        int w2 = width & ~1, h2 = height & ~1;
        var nv = new byte[width * height + width * (height / 2)];
        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * 4;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                int yv = (int)(16 + 0.257 * r + 0.504 * g + 0.098 * b + 0.5);
                nv[y * width + x] = (byte)Math.Clamp(yv, 0, 255);
            }
        }
        int chromaBase = width * height;
        for (int y = 0; y < h2; y += 2)
        {
            for (int x = 0; x < w2; x += 2)
            {
                double r = 0, g = 0, b = 0;
                for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                {
                    int i = ((y + dy) * width + (x + dx)) * 4;
                    b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2];
                }
                r /= 4; g /= 4; b /= 4;
                int cb = (int)(128 - 0.148 * r - 0.291 * g + 0.439 * b + 0.5);
                int cr = (int)(128 + 0.439 * r - 0.368 * g - 0.071 * b + 0.5);
                int o = chromaBase + (y / 2) * width + x;
                nv[o] = (byte)Math.Clamp(cb, 0, 255);
                nv[o + 1] = (byte)Math.Clamp(cr, 0, 255);
            }
        }
        return nv;
    }

    /// <summary>The MAP tab's red box at the top of the full map screen (see MapScreenWatcher in
    /// the Swift notes): share of "reddish" pixels in x 0.352–0.410, y 0.012–0.085 of the picture.</summary>
    public static double redTabFraction(byte[] bgra, int width, int height)
    {
        int x0 = (int)(0.352 * width), x1 = (int)(0.410 * width), y0 = (int)(0.012 * height), y1 = (int)(0.085 * height);
        int reddish = 0, total = 0;
        for (int y = y0; y < y1; y += 2)
            for (int x = x0; x < x1; x += 2)
            {
                int i = (y * width + x) * 4;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                total++;
                if (r >= 70 && r >= g + 30 && r >= b + 30) reddish++;
            }
        return total > 0 ? (double)reddish / total : 0;
    }

    /// <summary>Luma (grey) of a BGRA picture, for OCR of the HUD column, as a BGRA8 copy that
    /// the WinRT OCR accepts.</summary>
    public static byte[] grayBgra(byte[] bgra, int width, int height)
    {
        var o = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            byte l = (byte)Math.Clamp((int)(0.299 * r + 0.587 * g + 0.114 * b + 0.5), 0, 255);
            o[i] = l; o[i + 1] = l; o[i + 2] = l; o[i + 3] = 255;
        }
        return o;
    }
}

/// <summary>Writes a BGRA picture as a 32-bit BMP — the cheapest picture format with no
/// library, for reading the capture back over the mount.</summary>
public static class Bmp
{
    public static void write(string path, byte[] bgra, int width, int height)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Create);
            using var w = new BinaryWriter(f);
            int rowBytes = width * 4, imageSize = rowBytes * height;
            w.Write((byte)'B'); w.Write((byte)'M'); w.Write(54 + imageSize); w.Write(0); w.Write(54);
            w.Write(40); w.Write(width); w.Write(-height); w.Write((short)1); w.Write((short)32); w.Write(0); w.Write(imageSize); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
            w.Write(bgra, 0, imageSize);
        }
        catch (Exception e) { Log.log($"Could not write {path}: {e.Message}"); }
    }

    /// <summary>A gray picture (one byte per pixel, rowBytes stride) as BMP.</summary>
    public static void writeGray(string path, byte[] gray, int offset, int rowBytes, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte v = gray[offset + y * rowBytes + x];
                int i = (y * width + x) * 4;
                bgra[i] = v; bgra[i + 1] = v; bgra[i + 2] = v; bgra[i + 3] = 255;
            }
        write(path, bgra, width, height);
    }
}

static class Native
{
    public const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000, HALFTONE = 4;
    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(nint hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool GetClientRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(nint hWnd, ref POINT point);
    [DllImport("user32.dll")] public static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(nint hWnd, nint dc);
    [DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] public static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] public static extern nint CreateDIBSection(nint dc, ref BITMAPINFO info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] public static extern bool StretchBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll")] public static extern bool GdiFlush();
    [DllImport("user32.dll")] public static extern bool PrintWindow(nint hWnd, nint dc, uint flags);
    public const uint PW_CLIENTONLY = 1, PW_RENDERFULLCONTENT = 2;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }
}
