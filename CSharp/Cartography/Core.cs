// Sanctuary Sonar — Cartography, the C# port of the Swift library of the same name.
//
// Rules of the port (see ../../CLAUDE.md): a line-for-line translation, the same constants,
// the same algorithms, the same member names as the Swift source including their lower
// camel case, so that the two MapLabs print identical lines for the same recording. No
// improvements here; improvements go in both or neither.
namespace SanctuarySonar.Cartography;

/// <summary>A point in pixels, doubles, like Swift's CGPoint. Fields are lower case to match.</summary>
public struct Pt
{
    public double x, y;
    public Pt(double x, double y) { this.x = x; this.y = y; }
    public override string ToString() => $"({x:0},{y:0})";
}

/// <summary>A rectangle in whole pixels, like the Swift tuples (x, y, width, height).</summary>
public struct Rect
{
    public int x, y, width, height;
    public Rect(int x, int y, int width, int height) { this.x = x; this.y = y; this.width = width; this.height = height; }
}

/// <summary>A rectangle in doubles, like Swift's CGRect where the source used one.</summary>
public struct RectD
{
    public double x, y, width, height;
    public RectD(double x, double y, double width, double height) { this.x = x; this.y = y; this.width = width; this.height = height; }
    public bool contains(Pt p) => p.x >= x && p.x < x + width && p.y >= y && p.y < y + height;
}

public static class M
{
    public static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);
    /// <summary>Swift's atan2(y, x) argument order is the same as C#'s Math.Atan2(y, x).</summary>
    public static double Atan2(double y, double x) => Math.Atan2(y, x);
    /// <summary>Swift's truncatingRemainder: sign follows the dividend, like C#'s %.</summary>
    public static double TruncRem(double a, double b) => a % b;
}
