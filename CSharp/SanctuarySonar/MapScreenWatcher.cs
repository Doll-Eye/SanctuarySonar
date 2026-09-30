// Port of Sources/DungeonGuide/MapScreenWatcher.swift. Same names, same constants, same log lines.
//
// PORT NOTE: the Swift class captured a half-size picture of the whole window once a second,
// ran OCR on it and measured the MAP tab's red box itself; the shell captures, reads text and
// measures the red tab (Capture.cs, Ocr.cs) and hands both to `check(lines, redTab)`, which does
// what the Swift frame handler did after OCR. One worker thread (see Contracts.cs), so `armed`
// is a plain property and the callback is called synchronously.
using System.Globalization;
using SanctuarySonar.Cartography;

namespace SanctuarySonar.Shell;

/// <summary>
/// Notices the full dungeon map being opened, and reads its area list.
///
/// The owner, 26 Sep 2026: "when I say describe the map, I mean the one I pull up, the full
/// dungeon map, not the overhead one." The full map covers the minimap, so it is looked for
/// only while the minimap is unreadable (`armed`), once a second, in a half-size picture of
/// the whole window: its bottom bar reads "Center on Player … Pan … Zoom … Pin Location", and
/// its left panel lists the dungeon's areas under the dungeon's name in capitals ("FORBIDDEN
/// CITY" / "Path of Blood" / "Ghastly Depths" / "Unexplored Areas"). Reported once per
/// opening; closing the map (the minimap readable again) re-arms it.
/// </summary>
public sealed class MapScreenWatcher
{
    /// <summary>Called with the area names read from the panel, when the map opens.</summary>
    public Action<List<string>>? onMapOpened;

    /// <summary>True while the minimap is unreadable — the only time the map can be up.</summary>
    public bool armed { get; set; } = false;
    private bool reported = false;
    /// <summary>Frames looked at since arming: the first two that are not the map are logged, so a
    /// map that goes unnoticed leaves a trace (the 26 Sep run: opened twice, nothing logged).</summary>
    private int checks = 0;

    /// <summary>What `start(window:)` did besides the capture. PORT NOTE: the Swift logged the
    /// half-size capture's dimensions here; that is the shell's to log, since it captures.</summary>
    public void reset()
    {
        reported = false; checks = 0;
    }

    /// <summary>PORT NOTE: `stop()` stopped the capture, which is the shell's; a no-op here.</summary>
    public void stop() { }

    /// <summary>The minimap is readable again: the map, if it was up, is closed.</summary>
    public void disarm()
    {
        armed = false;
        reported = false; checks = 0;
    }

    /// <summary>One look at the window: the placed lines the OCR returned (text, x, y) and the
    /// red-tab fraction the shell measured (`redTabFraction` in the Swift). What the Swift frame
    /// handler did after OCR.</summary>
    public void check(IReadOnlyList<(string text, double x, double y)> lines, double redTab)
    {
        if (!armed || reported) return;
        var screen = MapScreen.read(lines);
        // A second signal that needs no text: the MAP tab's red box at the top of the screen
        // (27 Sep 2026, live: the bar read as "CerteT(thPkn)tT • 7M • O Ck*e" and three words
        // of ten was not the map). Two words and the red tab is.
        if (!screen.isMap && redTab >= redTabMinimum && MapScreen.promptWordCount(screen.bar) >= 2)
        {
            screen = MapScreen.read(lines, force: true);
        }
        checks += 1;
        if (!screen.isMap)
        {
            if (checks <= 2)
            {
                Log.log($"Map-screen check {checks}: {lines.Count} lines, red tab {redTab.ToString("F2", CultureInfo.InvariantCulture)}, bar \"{prefix(screen.bar, 80)}\" — not the map");
            }
            return;
        }
        reported = true;
        Log.log($"Full map open; areas: {string.Join(", ", screen.areas)}; red tab {redTab.ToString("F2", CultureInfo.InvariantCulture)}; bar \"{prefix(screen.bar, 80)}\"");
        onMapOpened?.Invoke(screen.areas);
    }

    /// <summary>The MAP tab's red box: x 0.352–0.410 of the window, y 0.047–0.081 with a title bar
    /// (0.014–0.049 full screen); measured 27 Sep 2026 at mean RGB (103, 50, 49), 64 % of
    /// the region "reddish" (R ≥ 70, R ≥ G + 30, R ≥ B + 30) on three map frames, 0 % on game
    /// frames. The band here covers both title-bar cases, so the share is lower.</summary>
    public const double redTabMinimum = 0.2;
    // PORT NOTE: `redTabFraction(_ pixels:)` — the share of reddish pixels in x 0.352–0.410,
    // y 0.012–0.085 of the BGRA frame, every second row and column, reddish = R ≥ 70 && R ≥ G + 30
    // && R ≥ B + 30 — is the shell's to measure on its own capture; `check` takes the result.

    /// <summary>Swift's `String(s.prefix(n))`. PORT NOTE: counts UTF-16 units, not grapheme clusters.</summary>
    private static string prefix(string s, int n) => s.Length > n ? s.Substring(0, n) : s;
}
