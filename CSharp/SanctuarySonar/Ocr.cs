// Text recognition through Windows.Media.Ocr, in the shapes the HUD reader wants: lines with
// their vertical position as a fraction of the picture (top = 0), or with x and y.
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace SanctuarySonar.Shell;

public sealed class HudTextReader
{
    readonly OcrEngine? engine;
    readonly object gate = new();

    public HudTextReader()
    {
        engine = OcrEngine.TryCreateFromLanguage(new Language("en-GB")) ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"))
              ?? OcrEngine.TryCreateFromUserProfileLanguages();
        Log.log(engine == null ? "Text recognition: no OCR language available (install an English language pack)"
                               : $"Text recognition ready ({engine.RecognizerLanguage.LanguageTag})");
    }

    public bool available => engine != null;

    /// <summary>Lines top to bottom with (text, y).</summary>
    public List<(string text, double y)> placedLines(byte[] bgra, int width, int height) =>
        placed(bgra, width, height).Select(l => (l.text, l.y)).ToList();

    /// <summary>Lines top to bottom with (text, x, y).</summary>
    public List<(string text, double x, double y)> placedLinesXY(byte[] bgra, int width, int height) => placed(bgra, width, height);

    List<(string text, double x, double y)> placed(byte[] bgra, int width, int height)
    {
        var result = new List<(string text, double x, double y)>();
        if (engine == null) return result;
        lock (gate)
        {
            try
            {
                // CreateCopyFromBuffer: the IMemoryBufferByteAccess cast failed under CsWinRT on the
                // first Windows run ("Invalid cast from WinRT.IInspectable"), so the pixels go in
                // through an IBuffer copy instead.
                using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
                var ocr = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
                foreach (var line in ocr.Lines)
                {
                    double minX = double.MaxValue, minY = double.MaxValue, maxX = 0, maxY = 0;
                    foreach (var w in line.Words)
                    {
                        minX = Math.Min(minX, w.BoundingRect.X); minY = Math.Min(minY, w.BoundingRect.Y);
                        maxX = Math.Max(maxX, w.BoundingRect.X + w.BoundingRect.Width); maxY = Math.Max(maxY, w.BoundingRect.Y + w.BoundingRect.Height);
                    }
                    if (minX == double.MaxValue) continue;
                    result.Add((line.Text, (minX + maxX) / 2 / width, (minY + maxY) / 2 / height));
                }
            }
            catch (Exception e) { Log.log($"Text recognition failed: {e.Message}"); }
        }
        result.Sort((a, b) => a.y.CompareTo(b.y));
        return result;
    }

}
