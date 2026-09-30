// Port of Sources/DungeonGuide/ObjectiveWatcher.swift. Same names, same logic, same log lines.
//
// PORT NOTE: the Swift class captured the tracker rectangle once a second and ran Vision OCR
// on it; the shell captures and reads text (Capture.cs, Ocr.cs) and hands the placed lines to
// `update(lines)`, which does what the Swift frame handler did after OCR. `WindowGeometry` is
// not ported: the shell computes rectangles with MinimapLayout / TrackerLayout. One worker
// thread (see Contracts.cs), so the OSAllocatedUnfairLock state is plain properties and the
// callbacks are called synchronously.
using SanctuarySonar.Cartography;

namespace SanctuarySonar.Shell;

/// <summary>
/// Reads the objective tracker under the minimap once a second and reports the dungeon's
/// objective when it changes.
///
/// Text recognition is noisy on a moving game picture, so a new reading is only believed
/// when two in a row agree. Counts ("Slay the Enraged Spirits: 2") are part of the
/// objective's text but not of its `kind`: a new kind is announced, a count is not — it is
/// there on request.
/// </summary>
public sealed class ObjectiveWatcher
{
    /// <summary>Called with each newly settled objective.</summary>
    public Action<Objective>? onObjective;
    /// <summary>Called when the area name above the minimap changes.</summary>
    public Action<string>? onArea;
    /// <summary>Called when the objective's count moves ("…: 3" → 2).</summary>
    public Action<int>? onCount;
    /// <summary>Called when a countdown in the HUD column changes (the Undercity).</summary>
    public Action<int>? onTimer;
    /// <summary>Called when the floor counter changes (the Undercity's "FLOOR 2/3").</summary>
    public Action<int, int>? onFloor;
    /// <summary>The objective as best read so far, with its count — for reading out on request.</summary>
    public (Objective? objective, int? count) current { get; private set; } = (null, null);
    /// <summary>Whether the tracker has said "Undercity" this run (HUDState.timedRun).</summary>
    public bool timedRun { get; private set; }
    /// <summary>The agreement rules, shared with MapLab.</summary>
    private readonly HUDState hud = new HUDState();
    /// <summary>Reads in a row that returned no text at all while the minimap was readable. Vision
    /// has failed silently before (26 Sep: no lines, no error, for a whole run); the guide
    /// is told after a minute of it so the owner knows the objective is not being followed.</summary>
    private int emptyReads = 0;
    public Action? onTextTrouble;
    // PORT NOTE: the Swift log line read `TrackerReader.lastError`; the OCR is the shell's here,
    // so the shell sets this to its last error text (null when none) before calling `update`.
    public string? lastError { get; set; }

    /// <summary>Set by the guide: false while the minimap is unreadable — the inventory, the map or
    /// a menu is up, and whatever text sits where the tracker was is not an objective.</summary>
    public bool mapVisible { get; set; } = true;

    /// <summary>What `start(window:)` did besides the capture: the agreement rules and the current
    /// reading start again. PORT NOTE: the Swift logged the tracker rectangle here; that is the
    /// shell's to log, since it computes it.</summary>
    public void reset()
    {
        hud.reset();
        current = (null, null);
    }

    /// <summary>PORT NOTE: `stop()` stopped the capture, which is the shell's; a no-op here.</summary>
    public void stop() { }

    /// <summary>One read of the tracker: the placed lines the OCR returned (text and y). What the
    /// Swift frame handler did after OCR.</summary>
    public void update(IReadOnlyList<(string text, double y)> lines)
    {
        if (!mapVisible) return;
        // Luma only. Not prepared with `Gray.textEnhanced`: the objective line is drawn in a
        // dimmer grey than the title, and the preparation left it too faint to read (26 Sep).
        // PORT NOTE: the Gray → CGImage → TrackerReader.placedLines step is the shell's.
        if (lines.Count == 0)
        {
            emptyReads += 1;
            if (emptyReads == 60)
            {
                Log.log("Objective text: no lines read for 60 s while the minimap was readable" +
                        (lastError is string e ? $"; last error {e}" : "; no error reported"));
                onTextTrouble?.Invoke();
            }
        }
        else
        {
            if (emptyReads >= 60) Log.log("Objective text: reading again");
            emptyReads = 0;
        }
        var changed = hud.update(lines);
        current = (hud.objective, hud.count);
        bool timedBefore = timedRun; timedRun = hud.timedRun;
        if (timedRun != timedBefore && hud.objective is Objective o) onObjective?.Invoke(o);
        if (changed.area is string area) onArea?.Invoke(area);
        if (changed.objective is Objective objective) onObjective?.Invoke(objective);
        if (changed.count is int count) onCount?.Invoke(count);
        if (changed.timer is int timer) onTimer?.Invoke(timer);
        if (changed.floor is { } floor) onFloor?.Invoke(floor.number, floor.of);
    }
}
