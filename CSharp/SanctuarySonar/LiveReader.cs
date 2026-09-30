// Port of Sources/DungeonGuide/LiveReader.swift. Same names, same constants, same logic.
//
// PORT NOTE: the Swift class captured the minimap itself with ScreenCaptureKit and processed
// frames on its own queue. Here the shell captures (Capture.cs) and hands each frame to
// `process(Frame)`, which does exactly what the Swift frame handler did from the point where
// the planes were in hand. Everything runs on the one worker thread (see Contracts.cs), so the
// OSAllocatedUnfairLock state is plain properties and the callbacks are called synchronously.
using System.Diagnostics;
using System.Globalization;
using SanctuarySonar.Cartography;

namespace SanctuarySonar.Shell;

/// <summary>
/// Reads the minimap of each frame, ten times a second, and keeps a stitched map and a route
/// to the nearest unexplored opening.
/// </summary>
public sealed class LiveReader
{
    public sealed class Snapshot
    {
        public bool legible;
        public double contrast;
        public Guidance? guidance;
        /// <summary>Direction of travel over the last 0.6 s, if moving.</summary>
        public double? heading;
        /// <summary>The character's facing from the minimap arrow, if it could be read.</summary>
        public double? facing = null;
        /// <summary>Bearing of the objective marker pinned to the minimap's edge, if one is showing.</summary>
        public double? markerBearing = null;
        /// <summary>The lead is a beacon or attunement pack because time is short.</summary>
        public bool toBeacon = false;
        /// <summary>Minimap pixels from the player to the nearest red mark, if any is showing.</summary>
        public double? nearestMark = null;
        /// <summary>What that time target is, for speech: "Beacon" or "Afflicted pack".</summary>
        public string timeTarget = "Beacon";
        /// <summary>The bearing of the side with floor if the way ahead is blocked, from the map;
        /// null when neither side has any.</summary>
        public double? sidestep = null;
        /// <summary>The map was started again on this frame.</summary>
        public bool newMap;
        /// <summary>How long reading, stitching and routing this frame took.</summary>
        public double milliseconds;
        /// <summary>Healing wells and arches confirmed on this frame: kind, straight-line bearing
        /// from the player (radians clockwise from up) and distance in minimap pixels.</summary>
        public List<(Landmark kind, double bearing, double distance)> newLandmarks = new();

        public Snapshot(bool legible, double contrast, Guidance? guidance, double? heading, bool newMap, double milliseconds)
        {
            this.legible = legible; this.contrast = contrast; this.guidance = guidance; this.heading = heading;
            this.newMap = newMap; this.milliseconds = milliseconds;
        }
    }

    public Action<Snapshot>? onSnapshot;
    // PORT NOTE: `onStopped` (the capture stopping) has no counterpart: the shell owns the capture.

    /// <summary>Set by the guide from the objective and the area: what to lead to, and the area rule.</summary>
    public Intent intent { get; set; } = Intent.none;

    /// <summary>The area name above the minimap changed.</summary>
    public void setArea(string name)
    {
        stitcher.setArea(name);
    }
    /// <summary>Set by the guide while leading back to the marked spot.</summary>
    public bool leadToSpot { get; set; } = false;
    /// <summary>The Undercity's countdown as last read, set by the guide; null when there is none.</summary>
    public int? timeLeft { get; set; } = null;
    /// <summary>Under this many seconds, a beacon or attunement pack in view is led to before the
    /// objective: each one lit bought about fifty seconds on 27 Sep, and the second floor
    /// began with 51 and ended with none lit.</summary>
    public const int shortTime = 40;
    /// <summary>Whether beacons (blue icons) are time targets at all. Off: afflicted packs only.
    /// Set by the shell from its settings. Mirrored in Swift on 29 Sep 2026.</summary>
    public static bool leadToBeacons = false;

    /// <summary>Marks the player's position; `done` gets whether there was one.</summary>
    public void markSpot(Action<bool> done)
    {
        var marked = stitcher.markSpot();
        done(marked);
    }

    /// <summary>What the map knows, relative to the player, for reading out: landmarks, the marked
    /// spot, remembered marks.</summary>
    public void describe(Action<string> done)
    {
        var parts = new List<string>();
        if (stitcher.player.HasValue)
        {
            Pt player = stitcher.player.Value;
            string place(Pt p)
            {
                var bearing = M.Atan2(p.x - player.x, -(p.y - player.y));
                return $"{Compass.word(bearing)}, {Guide.howFar(M.Hypot(p.x - player.x, p.y - player.y))}";
            }
            var landmarks = merged(stitcher.landmarkMemory.landmarks)
                .OrderBy(l => M.Hypot(l.point.x - player.x, l.point.y - player.y)).ToList();
            foreach (var l in landmarks) parts.Add($"{l.kind.rawValue} {place(l.point)}");
            if (landmarks.Count == 0) parts.Add("No healing well or arch seen yet");
            if (stitcher.spot is Pt spot) parts.Add($"Marked spot {place(spot)}");
            var marks = stitcher.markMemory.marks.Count;
            if (marks > 0) parts.Add($"{marks} marked enem{(marks == 1 ? "y" : "ies")}");
            // Where the unexplored ground is: the lead when it is an opening, and the rest.
            if (markerBearing is double b) parts.Add($"Objective marker {Compass.word(b)}, beyond the map");
            if (lastGuidance is Guidance g)
            {
                var lead = (g.toMark || g.toSpot || g.toArea) ? new List<(double bearing, double distance)>() : new List<(double bearing, double distance)> { (g.bearing, g.distance) };
                var words = Guide.directions(lead.Concat(g.others).ToList());
                parts.Add(words.Count == 0 ? "No unexplored openings in sight" : "Unexplored: " + string.Join("; ", words));
            }
            if (stitcher.areaNames.Count > 1) parts.Add("Areas seen: " + string.Join(", ", stitcher.areaNames));
        }
        var text = parts.Count == 0 ? "Nothing known on the map yet." : string.Join(". ", parts) + ".";
        done(text);
    }

    /// <summary>One entry per landmark: the map drifts over a long run and the same well is
    /// confirmed again a little way from where it was (four entries 90 px apart on 26 Sep).</summary>
    public static List<(Landmark kind, Pt point)> merged(List<(Landmark kind, Pt point)> landmarks)
    {
        var kept = new List<(Landmark kind, Pt point)>();
        foreach (var l in landmarks)
        {
            if (kept.Any(k => k.kind == l.kind && M.Hypot(k.point.x - l.point.x, k.point.y - l.point.y) < (l.kind == Landmark.healingWell ? 150 : 60))) continue;
            kept.Add(l);
        }
        return kept;
    }

    /// <summary>The guide found the player blocked while pushing along `facing`: the map learns a wall there.</summary>
    public void blocked(double facing)
    {
        stitcher.markBlocked(facing);
    }

    /// <summary>Whether a spot is marked on the current map.</summary>
    public void hasSpot(Action<bool> done)
    {
        var has = stitcher.spot != null;
        done(has);
    }
    private readonly Stitcher stitcher = new Stitcher();
    private readonly Navigator navigator = new Navigator();
    private double? refusedSince;
    /// <summary>After a floor change nothing is stitched until this time: the fade frames pass the
    /// legibility tests natively and seeded a map with phantom floor (30 Sep 2026).</summary>
    private double settleUntil = 0.0;
    public const double settleAfterReset = 2.5;
    /// <summary>The last plan, for describing where the unexplored ground is.</summary>
    private Guidance? lastGuidance;
    // PORT NOTE: the Swift kept `inset` and `box` (where the minimap box sits in the capture, and
    // its size) as fields set by `start`; the Frame carries both (`frame.inset`, `frame.box`).
    /// <summary>The objective marker's bearing and when it was last seen; held for `markerHold`.</summary>
    private double? markerBearing;
    private double markerSeen = -1.0;
    private double markerNearUntil = -1.0;
    /// <summary>The time target being led to, on the canvas: a pack's hourglasses move and split, and
    /// the nearest one changed every second (floor 1 of the 08:15 run: 10 reversals against
    /// 3 with the marker alone). Kept while any icon is within `timeTargetHold` of it.</summary>
    private Pt? lastTimeTarget;
    public const double timeTargetHold = 50.0;
    /// <summary>Time targets reached (the player came within `reached` px): a lit beacon keeps its
    /// icon, and on the 09:16 run the guide led to the one just lit, at the player's feet,
    /// for minutes — 44 reversals on floor 1. Nothing within `doneRadius` of one counts again.</summary>
    private List<Pt> doneTargets = new List<Pt>();
    public const double reached = 35.0;
    public const double doneRadius = 120.0;
    /// <summary>A time target's route may be this many times its straight distance, or 250 px, at
    /// most; a detour longer than that is not worth the time it buys.</summary>
    public const double detourRatio = 2.5;
    public const double detourCap = 250.0;
    /// <summary>An afflicted pack is a target only within this straight distance (they come to you);
    /// under `shortTime` any distance counts. (The 10:45 cut-back to "inside the box and
    /// within 100 px only" was reverted at 11:20 with the rest of that afternoon's changes:
    /// restore the state that reached floor 3 three times, then change one thing at a time.)</summary>
    public const double packReach = 120.0;
    public const double markerHold = 3.0;
    // PORT NOTE: `lastFrame` (the last Gray and reading) served only `saveMap`, which is dropped
    // with the PNG writing; it is not kept.
    // PORT NOTE: `startedAt` fed `time = began - startedAt`; the Frame's `time` is already seconds
    // since the guide started, so this only marks when `reset` last ran.
    private readonly Stopwatch startedAt = Stopwatch.StartNew();

    /// <summary>A legible minimap that fits nowhere on the map for this long is somewhere new — a
    /// new dungeon, a new floor, a teleport — and gets a new map.</summary>
    public const double newMapAfter = 2.5;
    public const int framesPerSecond = 10;

    /// <summary>Starts reading again from the next frame. PORT NOTE: the Swift `start(window:)`
    /// set up the capture and then did this on its queue; the capture is the shell's.</summary>
    public void reset()
    {
        stitcher.reset();
        navigator.forget();
        refusedSince = null;
        lastGuidance = null;
        markerBearing = null;
        lastTimeTarget = null;
        doneTargets = new List<Pt>();
        startedAt.Restart();
    }

    /// <summary>PORT NOTE: `stop()` stopped the capture, which is the shell's; a no-op here.</summary>
    public void stop() { }

    /// <summary>Throws the map away and starts again from the next frame.</summary>
    public void newMap()
    {
        stitcher.reset();
        navigator.forget();
        refusedSince = null;
        lastTimeTarget = null;
        doneTargets = new List<Pt>();
        markerBearing = null;
        settleUntil = startedAt.Elapsed.TotalSeconds + settleAfterReset;
    }

    // PORT NOTE: `saveMap(to:)` — the map and the last minimap as PNGs, for checking from outside —
    // is dropped: it needs Picture (CoreGraphics) and has no Windows counterpart yet.

    // MARK: Frames

    /// <summary>One captured frame. What the Swift `stream(_:didOutputSampleBuffer:)` did once the
    /// planes were in hand.</summary>
    public void process(Frame frame)
    {
        int width = frame.width, height = frame.height;
        // The capture is the minimap box plus a margin for edge-pinned icons; the reader
        // and the marks see the box, the marker finder the whole capture.
        // PORT NOTE: the Swift built `boxRect` from `inset` and `box` clipped to the plane;
        // `frame.box` is that rectangle, relative to the frame.
        Rect boxRect = frame.box;
        var gray = new Gray(frame.nv12, 0, frame.rowBytes, boxRect);
        Pt[] marks = new Pt[0];
        Pt? marker = null;
        Pt[] blue = new Pt[0];
        Pt[] orange = new Pt[0];
        var wantNow = intent;
        var @short = wantNow.timed && timeLeft is int left && left <= shortTime;
        // In a timed run the blue icons are always looked for: a beacon on the minimap is a
        // detour worth taking whatever the clock (the 08:39 runs walked past one for fifteen
        // seconds with the marker 300 px off, and arrived on floor 2 with 70 s).
        // PORT NOTE: the Swift's `if let chroma = …plane 1` is always true for NV12; the chroma
        // plane starts at rowBytes × height in `nv12`, with the same rowBytes.
        int chromaOffset = frame.rowBytes * height;
        var outer = new Rect(0, 0, width, height);
        {
            marks = RedMarks.find(frame.nv12, chromaOffset, frame.rowBytes, boxRect);
            marker = ObjectiveMarker.find(frame.nv12, 0, frame.rowBytes,
                                          frame.nv12, chromaOffset, frame.rowBytes,
                                          outer, frame.inset);
            if (wantNow.timed)
            {
                blue = BlueIcons.find(frame.nv12, 0, frame.rowBytes,
                                      frame.nv12, chromaOffset, frame.rowBytes,
                                      outer, frame.inset);
                orange = OrangeIcons.find(frame.nv12, 0, frame.rowBytes,
                                          frame.nv12, chromaOffset, frame.rowBytes,
                                          outer, frame.inset);
            }
        }

        var began = Stopwatch.StartNew();
        double time = frame.time;
        var reading = MinimapReader.read(gray);
        reading.marks = marks;
        // Short on time with a beacon in sight: the beacon is the target instead.
        // …unless the objective marker is itself on the minimap: the boss room in reach
        // beats a beacon (floor 1 of the 08:00 run was won with 40 s left that way).
        var beaconLead = false;
        var timeTarget = "Beacon";
        Pt? objectiveMarker = marker;
        const double inset = 40.0;
        if (marker.HasValue && marker.Value.x > inset && marker.Value.y > inset && marker.Value.x < (double)boxRect.width - inset && marker.Value.y < (double)boxRect.height - inset)
        {
            markerNearUntil = time + markerHold;     // it jitters in and out of the inset
        }
        var markerNear = time < markerNearUntil;
        if (!markerNear && reading.arrow.HasValue)
        {
            Pt arrow = reading.arrow.Value;
            // A beacon inside the box is near enough to take at any time; a pinned one only
            // when the clock is short.
            // Beacons and afflicted packs alike: both buy time. Orange packs pin to the edge too.
            // Beacons only when asked for: the owner cannot light them by feel (28 Sep 2026, three
            // beacon leads on one floor, two reached, no time gained), so by default the time
            // targets are the afflicted packs alone. Mirrored in Swift on 29 Sep 2026 (`LiveReader.leadToBeacons`).
            var beacons = leadToBeacons ? blue.Select(p => (p, "Beacon")) : Enumerable.Empty<(Pt, string)>();
            var all = beacons.Concat(orange.Select(p => (p, "Afflicted pack"))).ToList();
            var inside = all.Where(c => c.Item1.x > 20 && c.Item1.y > 20 && c.Item1.x < (double)boxRect.width - 20 && c.Item1.y < (double)boxRect.height - 20).ToList();
            var candidates = @short ? all : inside;
            if (stitcher.player.HasValue)
            {
                Pt player = stitcher.player.Value;
                Func<Pt, Pt> canvas = p => new Pt(player.x + p.x - arrow.x, player.y + p.y - arrow.y);
                // Reached: the current target with the player on top of it is done.
                if (lastTimeTarget.HasValue && M.Hypot(lastTimeTarget.Value.x - player.x, lastTimeTarget.Value.y - player.y) < reached)
                {
                    doneTargets.Add(lastTimeTarget.Value);
                    lastTimeTarget = null;
                }
                candidates = candidates.Where(c =>
                {
                    var p = canvas(c.Item1);
                    if (doneTargets.Any(d => M.Hypot(d.x - p.x, d.y - p.y) < doneRadius)) return false;
                    if (M.Hypot(c.Item1.x - arrow.x, c.Item1.y - arrow.y) < reached) return false;
                    if (c.Item2 != "Beacon" && !@short && M.Hypot(c.Item1.x - arrow.x, c.Item1.y - arrow.y) > packReach) return false;
                    return true;
                }).ToList();
            }
            // The one already being led to, if it is still about; else the nearest.
            (Pt, string)? chosen = null;
            if (lastTimeTarget.HasValue && stitcher.player.HasValue)
            {
                Pt last = lastTimeTarget.Value, player = stitcher.player.Value;
                chosen = nearest(candidates.Where(c => M.Hypot(player.x + c.Item1.x - arrow.x - last.x, player.y + c.Item1.y - arrow.y - last.y) < timeTargetHold), arrow);
            }
            if (chosen == null)
            {
                chosen = nearest(candidates, arrow);
            }
            if (chosen.HasValue)
            {
                marker = chosen.Value.Item1;
                beaconLead = true;
                timeTarget = chosen.Value.Item2;
                if (stitcher.player.HasValue) { Pt player = stitcher.player.Value; lastTimeTarget = new Pt(player.x + chosen.Value.Item1.x - arrow.x, player.y + chosen.Value.Item1.y - arrow.y); }
            }
            else
            {
                lastTimeTarget = null;
            }
        }
        // The cyan objective marker is an Undercity thing. In ordinary dungeons the only cyan
        // seen so far is a chest icon at the entrance (Forbidden City, 27 Sep replay), which
        // pulled the lead for the first minute; until a recording shows a real one, none.
        if (!wantNow.timed) marker = null;
        Pt? markerInBox = null;
        if (marker.HasValue && reading.arrow.HasValue)
        {
            Pt m = marker.Value, arrow = reading.arrow.Value;
            markerBearing = M.Atan2(m.x - arrow.x, -(m.y - arrow.y));
            markerSeen = time;
            // Well inside the box, it is a place on the map, not a direction.
            // 40 px: the pinned icon, ring and all, sits up to ~20 px inside the border.
            if (m.x > inset && m.y > inset && m.x < (double)boxRect.width - inset && m.y < (double)boxRect.height - inset)
            {
                markerInBox = m;
            }
        }
        else if (markerBearing != null && time - markerSeen > markerHold)
        {
            markerBearing = null;
        }
        if (time < settleUntil || !reading.isLegible)   // settling after a floor change, or unreadable
        {
            publish(new Snapshot(legible: false, contrast: reading.contrast, guidance: null, heading: null, newMap: false,
                                 milliseconds: began.Elapsed.TotalMilliseconds));
            return;
        }
        var newMap = false;
        var placement = stitcher.add(reading, time);
        if (placement.placed)
        {
            refusedSince = null;
        }
        else if (refusedSince is double since)
        {
            if (time - since > newMapAfter)
            {
                Log.log($"Minimap fits nowhere for {newMapAfter.ToString(CultureInfo.InvariantCulture)} s (best score {placement.score.ToString("F2", CultureInfo.InvariantCulture)}): new map");
                stitcher.reset();
                navigator.forget();
                _ = stitcher.add(reading, time);
                refusedSince = null;
                newMap = true;
            }
        }
        else
        {
            refusedSince = time;
        }
        var want = intent;
        var rule = Navigator.AreaRule.none;
        if (want.stayIn is string s && stitcher.areaIndex(s) is int si) rule = Navigator.AreaRule.stay(si);
        if (want.leave is string l && stitcher.areaIndex(l) is int li) rule = Navigator.AreaRule.leave(li);
        // "Travel to the Siren's Chamber" when the Siren's Chamber has been walked through:
        // straight there. On 26 Sep the guide looked for new ground instead, for three
        // minutes, with the border 200 px away.
        if (want.destination is string d && stitcher.areaMatching(d) is int di && di != stitcher.currentArea)
        {
            rule = Navigator.AreaRule.goTo(di, want.leave is string lv ? stitcher.areaIndex(lv) : null);
        }
        // The frame sits on the canvas with its arrow at the player, so a point in the box
        // is the player plus its offset from the arrow.
        Pt? markerPoint = null;
        if (markerInBox.HasValue && reading.arrow.HasValue && stitcher.player.HasValue)
        {
            Pt m = markerInBox.Value, a = reading.arrow.Value, player = stitcher.player.Value;
            markerPoint = new Pt(player.x + m.x - a.x, player.y + m.y - a.y);
        }
        var guidance = navigator.plan(stitcher, toMarks: want.slay, toSpot: leadToSpot,
                                      toArches: want.travel && !want.timed, areaRule: rule, marker: markerBearing,
                                      markerPoint: markerPoint, timed: want.timed);
        // A time target whose route is a long way round is not worth it: plan again for the
        // objective instead (the 09:16 run: a beacon 60 px away across a wall, 424 px of route).
        if (beaconLead && guidance.HasValue && (markerPoint ?? markerInBox).HasValue && stitcher.player.HasValue)
        {
            Guidance g = guidance.Value;
            Pt m = (markerPoint ?? markerInBox).Value;
            var straight = 150.0;
            if (markerInBox != null && reading.arrow.HasValue) { Pt a = reading.arrow.Value; straight = M.Hypot(m.x - a.x, m.y - a.y); }
            if (g.distance > Math.Max(detourCap, detourRatio * straight))
            {
                beaconLead = false;
                lastTimeTarget = null;
                double? objective = (objectiveMarker.HasValue && reading.arrow.HasValue)
                    ? M.Atan2(objectiveMarker.Value.x - reading.arrow.Value.x, -(objectiveMarker.Value.y - reading.arrow.Value.y))
                    : null;
                guidance = navigator.plan(stitcher, toMarks: want.slay, toSpot: leadToSpot,
                                          toArches: want.travel && !want.timed, areaRule: rule, marker: objective, markerPoint: null, timed: want.timed);
            }
        }
        lastGuidance = guidance;
        var landmarks = new List<(Landmark kind, double bearing, double distance)>();
        if (placement.placed && stitcher.player.HasValue)
        {
            Pt player = stitcher.player.Value;
            landmarks = stitcher.newLandmarks.Select(kp =>
                (kp.Item1, M.Atan2(kp.Item2.x - player.x, -(kp.Item2.y - player.y)), M.Hypot(kp.Item2.x - player.x, kp.Item2.y - player.y))
            ).ToList();
        }
        double? nearestMark = null;
        if (reading.arrow is Pt ap0 && reading.marks.Any()) nearestMark = reading.marks.Min(m => M.Hypot(m.x - ap0.x, m.y - ap0.y));
        publish(new Snapshot(legible: true, contrast: reading.contrast, guidance: guidance,
                             heading: stitcher.heading(), newMap: newMap, milliseconds: began.Elapsed.TotalMilliseconds)
        {
            facing = reading.facing, markerBearing = markerBearing, toBeacon = beaconLead, timeTarget = timeTarget,
            nearestMark = nearestMark,
            sidestep = reading.facing is double f ? stitcher.sidestep(f) : null,
            newLandmarks = landmarks,
        });
    }

    /// <summary>Swift's `min(by:)` on distance from the arrow: the first of equal minima.</summary>
    private static (Pt, string)? nearest(IEnumerable<(Pt, string)> candidates, Pt arrow)
    {
        (Pt, string)? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var c in candidates)
        {
            var distance = M.Hypot(c.Item1.x - arrow.x, c.Item1.y - arrow.y);
            if (best == null || distance < bestDistance) { best = c; bestDistance = distance; }
        }
        return best;
    }

    private void publish(Snapshot snapshot)
    {
        // PORT NOTE: the Swift hopped to the main queue; one worker thread here, so direct.
        onSnapshot?.Invoke(snapshot);
    }
}
