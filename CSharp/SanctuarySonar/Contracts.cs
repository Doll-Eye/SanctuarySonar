// Sanctuary Sonar — the Windows shell around the shared Cartography core.
//
// These are the seams between the ported guide logic (Guide.cs, LiveReader.cs,
// ObjectiveWatcher.cs, MapScreenWatcher.cs, BeaconSynth.cs — ports of the Swift files of the
// same names) and the Windows layer (Capture.cs, Ocr.cs, Speech.cs, BeaconOut.cs, HotKeys.cs,
// Program.cs). The ports know nothing of Windows; the Windows layer knows nothing of maps.
//
// Threading: everything runs on one worker thread (the capture loop) except audio rendering,
// which pulls from BeaconSynth on the audio thread through a lock-free parameter copy. Hot
// keys post actions onto the worker thread's queue. So the ports need no locks of their own.
using SanctuarySonar.Cartography;

namespace SanctuarySonar.Shell;

/// <summary>Speech for what the screen reader cannot already know. Says nothing when no screen
/// reader is running, as the Mac version does with VoiceOver off.</summary>
public interface ISpeech
{
    void say(string text);
    bool screenReaderRunning { get; }
}

public enum BeaconAccuracy { away = 0, near = 1, on = 2 }

/// <summary>The directional beacon: pan from the bearing, pitch from north/south, rhythm from accuracy.</summary>
public interface IBeacon
{
    void point(double bearing, BeaconAccuracy accuracy);
    void silence();
    double volume { get; set; }
    void startEngine();
}

/// <summary>One captured frame of the game window's HUD column, NV12: luma plane (rowBytes ×
/// height) then Cb/Cr interleaved at half size. `box` is the minimap rectangle within this
/// frame, `inset` where the box sits inside the captured (outer) rectangle, `time` seconds
/// since the guide started.</summary>
public struct Frame
{
    public byte[] nv12;
    public int width, height, rowBytes;
    public Rect box;
    public (int x, int y) inset;
    public double time;
}

public static class Log
{
    public static Action<string> sink = Console.Error.WriteLine;
    public static void log(string text) => sink(text);
}

/// <summary>Small persisted settings (the beacon volume, the game window's title), one
/// `key=value` per line in a text file beside the log.</summary>
public static class Settings
{
    static readonly Dictionary<string, string> values = new();
    static readonly System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
    public static string? path;
    public static double getDouble(string key, double fallback) =>
        values.TryGetValue(key, out var v) && double.TryParse(v, System.Globalization.NumberStyles.Float, invariant, out var d) ? d : fallback;
    public static string getString(string key, string fallback) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;
    public static void set(string key, double value) => set(key, value.ToString(invariant));
    public static void set(string key, string value) { values[key] = value; save(); }
    public static void load()
    {
        try
        {
            if (path == null || !File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var eq = line.IndexOf('=');
                if (eq > 0) values[line[..eq]] = line[(eq + 1)..];
            }
        }
        catch { }
    }
    static void save()
    {
        try { if (path != null) File.WriteAllLines(path, values.Select(kv => $"{kv.Key}={kv.Value}")); } catch { }
    }
}
