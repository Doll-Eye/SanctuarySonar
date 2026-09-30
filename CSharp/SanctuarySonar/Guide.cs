// Sanctuary Sonar — Guide, the C# port of Sources/DungeonGuide/Guide.swift.
//
// A line-for-line translation: the same constants, the same decisions, the same words spoken,
// the same member names as the Swift source including their lower camel case. What differs
// is marked `PORT NOTE`: the window capture and the controller haptics belong to the Mac, and
// speech and the beacon come in through the contracts in Contracts.cs.
using System.Globalization;
using SanctuarySonar.Cartography;
using Snapshot = SanctuarySonar.Shell.LiveReader.Snapshot;

namespace SanctuarySonar.Shell;

/// <summary>Leads to the nearest unexplored opening by sound, while the game is in front.
///
/// Speech only for changes nobody could otherwise know about: the guide going on or off,
/// the map being lost for more than a moment, a new map, nothing left to explore in sight.
/// The beacon carries everything continuous. Silence whenever the minimap cannot be read —
/// a wrong direction is worse than none.</summary>
public sealed class Guide
{
    public bool isOn { get; private set; } = false;
    public string status { get; private set; } = "Guide off.";

    readonly ISpeech speech;
    readonly IBeacon beacon;
    readonly LiveReader reader;
    // PORT NOTE: the Swift `Haptics` (DualSense grips over USB) has no Windows counterpart yet;
    // every haptic cue is a one-line comment where it was.
    readonly ObjectiveWatcher objectives;
    readonly MapScreenWatcher mapScreen;
    /// <summary>The dungeon's objective as last read from the tracker.</summary>
    public Objective? objective { get; private set; }
    /// <summary>The area name above the minimap, as last read.</summary>
    string? area;
    bool wasOnCourse = false;

    // PORT NOTE: the Swift `vibration` switch (mirrored from Haptics for the window) is gone with the haptics.
    DateTime lastLegible = DateTime.UtcNow;
    bool lostAnnounced = false;
    /// <summary>Whether any frame has been readable since the guide started. If none has, the likely
    /// cause is the game's Map Display set to the overlay, and saying so beats "lost".</summary>
    bool everLegible = false;
    DateTime? noOpeningSince;
    bool noOpeningAnnounced = false;
    Snapshot? latest;
    /// <summary>The opening being led to, and how near it was on the last snapshot — when it vanishes
    /// while near, it was a dead end, and the owner needs to hear that.</summary>
    (Pt target, double distance, int epoch)? leadingTo;
    DateTime lastDeadEnd = DateTime.MinValue;
    DateTime lastNoOpening = DateTime.MinValue;
    bool ledToMark = false;
    bool ledToArch = false;
    bool ledToWell = false;
    bool ledToArea = false;
    bool ledToMarker = false;
    bool markerWasInView = false;
    bool ledToBeacon = false;
    /// <summary>Not moving, while facing along the route: since when. Standing still with the stick
    /// pushed the right way is a wall, a crate, a ledge — "something blocking my path,
    /// couldn't hear footsteps" (the owner, 27 Sep). Said after `blockedAfter`, then every
    /// `blockedRepeat` while it lasts.</summary>
    DateTime? pushingSince;
    double? pushingFacing;
    DateTime mapBegan = DateTime.MinValue;
    /// <summary>Steering round what the minimap cannot show. Stopped for `steerAfter` while pushing
    /// along the route, the beacon itself swings to one side — the side the map says, else
    /// 50° off the route — and stays there until the player has been moving again for
    /// `steerRelease`; still stuck after `steerSwap`, it tries the other side. The overworld's
    /// own beacon walks the game's navigation mesh round every crate; this is the cane
    /// version of that (the owner, 27 Sep: "it needs to do it on the micro scale").</summary>
    double? steerBearing;
    double steerSide = 1.0;
    DateTime steerSince = DateTime.MinValue;
    DateTime? movingSince;
    public static readonly double steerAfter = 1.0;
    public static readonly double steerSwap = 2.0;
    public static readonly double steerRelease = 0.6;
    public static readonly double steerAngle = 40.0 * Math.PI / 180;
    /// <summary>The spoken "Blocked. Try …" — off while the beacon steers (the owner, 27 Sep: "I don't
    /// need the voice announcing"); the map is no longer painted from it either, since a
    /// wall painted during a fight or a pick-up is a phantom the route then avoids all floor.</summary>
    public static readonly bool speakBlocked = false;
    DateTime lastBlockedSaid = DateTime.MinValue;
    public static readonly double blockedAfter = 2.5;
    public static readonly double blockedRepeat = 6;
    /// <summary>When the marker was last announced, either way: the pinned icon and the in-view
    /// state both flicker, and "Objective marker on the map" was said three times in five
    /// seconds on the 08:15 run.</summary>
    DateTime lastMarkerSaid = DateTime.MinValue;
    public static readonly double markerGap = 20;
    /// <summary>The lead's compass word as last spoken, and the one waiting to be: said when it has
    /// held for `directionHold` and the last was `directionGap` ago ("it should be telling me
    /// directions more verbosely", the owner, 27 Sep).</summary>
    string? spokenDirection;
    double? spokenBearing;
    (string word, DateTime since)? directionCandidate;
    DateTime lastDirectionSaid = DateTime.MinValue;
    public static readonly double directionHold = 2;
    public static readonly double directionGap = 5;
    /// <summary>A new word is spoken only when the bearing has moved this far from the one spoken
    /// last: on the 07:25 run the lead sat on the west / north-west boundary and the guide
    /// said "West… North-west… West…" every few seconds.</summary>
    public static readonly double directionTurn = 60.0 * Math.PI / 180;
    /// <summary>Area names the tracker has shown this dungeon, for correcting the map panel's reading.</summary>
    List<string> knownAreas = new();
    /// <summary>The Undercity's countdown as last read, for saying the thresholds once each.</summary>
    int? lastTimer;
    /// <summary>When the timer was last read. Once the run is over the badge goes ("Time
    /// expiration is imminent!" replaces it), and on 28 Sep the guide said "Objective south,
    /// close. 10 seconds" twice in town from a reading half a minute old. A reading older than
    /// `timerStale` counts for nothing.</summary>
    DateTime lastTimerRead = DateTime.MinValue;
    public static readonly double timerStale = 6;
    int? freshTimer => (DateTime.UtcNow - lastTimerRead).TotalSeconds <= timerStale ? lastTimer : null;
    /// <summary>When the timer last went up — a kill or a beacon paying. "This fight is not
    /// paying" needs it to have stood still for `notPayingAfter` with the clock short.</summary>
    DateTime lastTimerRise = DateTime.MinValue;
    DateTime lastNotPaying = DateTime.MinValue;
    public static readonly double notPayingAfter = 15;
    public static readonly double notPayingGap = 15;
    /// <summary>Gap between two time-target announcements: at 8 s a pack moving round the
    /// player was called eight times in 70 s (run 1, 28 Sep).</summary>
    public static readonly double timeTargetGap = 20;
    /// <summary>A red mark nearer than this (minimap px) is a fight, not a wall: no steering.</summary>
    public static readonly double enemyNear = 60;
    /// <summary>Within this many minimap px of the objective marker's point the player is there: quiet
    /// beacon, "At the objective marker" once, no direction words or steering (30 Sep 2026).</summary>
    public static readonly double markerArrived = 30;
    bool saidAtMarker = false;
    DateTime? enemiesCloseSince;
    DateTime lastEnemiesSaid = DateTime.MinValue;
    public static readonly double enemiesAfter = 4;
    public static readonly double enemiesGap = 20;
    /// <summary>A travel objective just began: say "Looking outside …" with the first lead, unless
    /// that lead is straight to the area itself.</summary>
    string? leaveToSay;
    /// <summary>While leading back: when there stopped being a route to the spot, if there is none.</summary>
    DateTime? spotRouteMissingSince;
    /// <summary>Within this many minimap pixels of the spot, the owner is back.</summary>
    public static readonly double arrived = 45.0;
    Dictionary<Landmark, DateTime> announcedLandmarks = new();
    List<(string kind, DateTime at)> announcedObjectives = new();
    public static readonly double announceGap = 60;
    /// <summary>When red marks were last being led to — "Marked enemies" is said only after a gap.</summary>
    DateTime lastMarkLead = DateTime.MinValue;
    /// <summary>When guidance last existed; a gap shorter than `holdGap` keeps the beacon going, so
    /// an opening flickering at the edge of the map does not stutter the sound.</summary>
    DateTime lastGuidance = DateTime.MinValue;
    DateTime lastLog = DateTime.MinValue;

    /// <summary>How long the map may be unreadable before it is said out loud — menus, the full
    /// map and loading screens are usually shorter than this.</summary>
    public static readonly double lostAfter = 3;
    public static readonly double noOpeningAfter = 3;
    public static readonly double noOpeningRepeat = 30;
    public static readonly double holdGap = 2;
    /// <summary>An opening that disappears while its route is shorter than this was a dead end.</summary>
    // 80 since 30 Sep 2026: on the native render openings close at 110–150 px before the player is there.
    public static readonly double deadEndNear = 80;
    /// <summary>How closely the direction of travel must match the route to count as on course.</summary>
    public static readonly double onCourseAngle = 25.0 * Math.PI / 180;
    public static readonly double nearAngle = 60.0 * Math.PI / 180;

    /// <summary>The beacon's volume, 0…1, for the window's slider and the louder/quieter keys.</summary>
    // PORT NOTE: the Swift setter also sent `objectWillChange`; the shell persists the beacon's own
    // `volume`, so nothing is published from here.
    public double beaconVolume
    {
        get => beacon.volume;
        set => beacon.volume = value;
    }

    public void louder() => changeVolume(0.1);
    public void quieter() => changeVolume(-0.1);

    void changeVolume(double step)
    {
        beaconVolume = (beaconVolume + step).clamped(0, 1);
        tell($"Beacon {(int)Math.Round(beaconVolume * 100, MidpointRounding.AwayFromZero)} percent.", "Pop");
    }

    public Action? onChange;

    /// <summary>For `--guide-test`: no speech, no sounds, no beacon — everything else as live.</summary>
    public bool silentTest = false;

    public Guide(ISpeech speech, IBeacon beacon, LiveReader reader, ObjectiveWatcher objectives, MapScreenWatcher mapScreen)
    {
        this.speech = speech;
        this.beacon = beacon;
        this.reader = reader;
        this.objectives = objectives;
        this.mapScreen = mapScreen;
        // PORT NOTE: `vibration = haptics.enabled` is gone with the haptics.
        reader.onSnapshot = snapshot => handle(snapshot);
        objectives.onObjective = @new => objectiveChanged(@new);
        objectives.onArea = name => areaChanged(name);
        objectives.onCount = count => countChanged(count);
        objectives.onTimer = seconds => timerChanged(seconds);
        objectives.onFloor = (number, of) => floorChanged(number, of);
        mapScreen.onMapOpened = areas => describeMap(areas);
        objectives.onTextTrouble = () =>
        {
            tell("The objective text can't be read at the moment, so the guide is exploring by itself.", "Basso");
        };
        // PORT NOTE: the Swift `reader.onStopped` (capture ended → `finish(saying:)`) has no
        // counterpart in the C# LiveReader; the shell owns capture and calls `stop()` itself.
    }

    // PORT NOTE: the Swift `start(_ target:)` took the chosen window and started three captures;
    // here capture belongs to the shell, so this only resets state and turns the guide on.
    public void start()
    {
        if (isOn) return;
        reader.reset();
        Log.log("Guide on.");
        objective = null;
        beacon.startEngine();
        // The controller is left alone entirely unless vibration is on. (Haptics: not on Windows.)
        wasOnCourse = false;
        isOn = true;
        lastLegible = DateTime.UtcNow;
        lostAnnounced = false;
        everLegible = false;
        leadingTo = null;
        // Until the tracker is read, lead to openings; the first objective sets this.
        ledToMark = false;
        reader.intent = Intent.none;
        reader.leadToSpot = false;
        ledToArch = false;
        ledToWell = false;
        ledToArea = false;
        ledToMarker = false;
        ledToBeacon = false;
        pushingSince = null;
        reader.timeLeft = null;
        spokenDirection = null;
        spokenBearing = null;
        directionCandidate = null;
        knownAreas = new();
        lastTimer = null;
        lastTimerRead = DateTime.MinValue; lastTimerRise = DateTime.MinValue; lastNotPaying = DateTime.MinValue;
        floorSeen = null;
        mapBegan = DateTime.UtcNow;   // no "blocked" or dead end in the first seconds of a run either
        saidAtMarker = false;
        leaveToSay = null;
        announcedObjectives = new();
        noOpeningSince = null;
        noOpeningAnnounced = false;
        // PORT NOTE: the Swift status named the app being read ("Guide on, reading <app>.");
        // the shell knows the window here, the guide does not.
        status = "Guide on.";
        tell("Guide on.", "Tink");
        onChange?.Invoke();
    }

    public void stop()
    {
        if (!isOn) return;
        finish("Guide off.");
    }

    void finish(string text)
    {
        // PORT NOTE: the Swift saved the stitched map as a PNG (`reader.saveMap`) and stopped the
        // three captures here; capture and the map picture belong to the shell on Windows.
        beacon.silence();
        isOn = false;
        status = text;
        tell(text, "Bottle");
        onChange?.Invoke();
    }

    // PORT NOTE: `testVibration()` (left grip, right grip, on-course glide) is gone with the haptics.

    /// <summary>Marks where the owner is standing, to be led back to later.</summary>
    public void markSpot()
    {
        if (!isOn) { tell("Guide is off.", "Basso"); return; }
        reader.markSpot(marked =>
        {
            tell(marked ? "Spot marked." : "Can't mark it yet: the map isn't read.", marked ? "Pop" : "Basso");
        });
    }

    /// <summary>Leads back to the marked spot; pressed again, goes back to what it was doing.</summary>
    public void takeMeBack()
    {
        if (!isOn) { tell("Guide is off.", "Basso"); return; }
        if (reader.leadToSpot)
        {
            reader.leadToSpot = false;
            tell("Stopped leading back.", "Pop");
            return;
        }
        reader.hasSpot(has =>
        {
            if (!has) { tell("No spot marked.", "Basso"); return; }
            reader.leadToSpot = true;
            spotRouteMissingSince = null;
            tell("Leading back to the marked spot.", "Pop");
        });
    }

    public void newMap()
    {
        if (!isOn) { tell("Guide is off.", "Basso"); return; }
        reader.newMap();
        tell("New map.", "Pop");
    }

    /// <summary>A new objective kind is said; a changed count is only logged (it is there on
    /// request). The first objective after starting is said too, so the owner knows what
    /// the guide believes it is.</summary>
    void objectiveChanged(Objective @new)
    {
        // A boss's name shown above the tracker pushes every line down one: "Blood Magus"
        // read as the place and "Forbidden City" as the objective (25 Sep). An objective
        // that is the dungeon's own name is that, not an objective.
        if (objective?.place is string place && @new.text == place)
        {
            Log.log($"Ignored tracker reading {@new.place} → {@new.text}: shifted by a line above");
            return;
        }
        var previous = objective;
        objective = @new;
        Log.log($"Objective: {@new.place} → {@new.text}");
        // Said when it is a different objective from the last, and from any said in the
        // last five minutes. The same words give or take a few letters are the same
        // objective: "Seaborn", "Seabom", "Gpddess" and "Goildess" were each announced on
        // the 26 Sep Mariner's Refuge run, fourteen times for one objective.
        bool differs = previous is { } p ? !(Objective.alike(p.kind, @new.kind) && Objective.alike(p.place, @new.place)) : true;
        bool saidLately = announcedObjectives.Any(a => Objective.alike(a.kind, @new.kind) && (DateTime.UtcNow - a.at).TotalSeconds < 300);
        if (isOn && differs && !saidLately)
        {
            announcedObjectives.Add((@new.kind, DateTime.UtcNow));
            tell($"Objective: {spoken(@new, @new.count)}.", "Glass");
        }
        updateIntent();
    }

    /// <summary>The tracker's count moved: "Destroy the Seaborn Goddess: 3" became 2. The green
    /// number is the one piece of progress a sighted player watches.</summary>
    void countChanged(int count)
    {
        Log.log($"Count: {count}");
        if (!(isOn && count > 0)) return;
        tell($"{count} to go.", "Glass");
    }

    /// <summary>The Undercity's countdown moved. Said at a minute, thirty seconds and ten, once each
    /// on the way down, and whenever it jumps up — time bought by igniting a beacon or a
    /// kill. Sighted players glance at the badge; nothing else says it.</summary>
    void timerChanged(int seconds)
    {
        try
        {
            Log.log($"Timer: {seconds}");
            reader.timeLeft = seconds;
            lastTimerRead = DateTime.UtcNow;
            if (lastTimer is int prev && seconds > prev) lastTimerRise = DateTime.UtcNow;
            if (!isOn) return;
            if (lastTimer is int last && seconds >= last + 15)
            {
                tell($"Time {seconds}.", null);
                return;
            }
            foreach (int mark in new[] { 60, 30, 10 })
            {
                if (seconds <= mark && (lastTimer ?? int.MaxValue) > mark)
                {
                    tell($"{mark} seconds.", null);
                    return;
                }
            }
        }
        finally
        {
            lastTimer = seconds;
        }
    }

    /// <summary>The Undercity's floor counter moved: a new floor is a new map (on 27 Sep the second
    /// floor was stitched onto the first as if next door), and worth hearing.</summary>
    int? floorSeen;
    void floorChanged(int number, int of)
    {
        Log.log($"Floor: {number} of {of}");
        try
        {
            if (!isOn) return;
            if (floorSeen != null) { reader.newMap(); mapBegan = DateTime.UtcNow; saidAtMarker = false; }
            tell($"Floor {number} of {of}.", "Glass");
        }
        finally
        {
            floorSeen = number;
        }
    }

    /// <summary>The objective's words with its count as progress: "Destroy the Seaborn Goddess, 3 to go".</summary>
    public static string spoken(Objective objective, int? count)
    {
        return count is int c ? (c > 0 ? $"{objective.kind}, {c} to go" : objective.kind) : objective.kind;
    }

    /// <summary>The area name above the minimap changed. The game's screen reader announces zones,
    /// so nothing is said; the map records it and the intent is worked out again.</summary>
    void areaChanged(string name)
    {
        Log.log($"Area: {name}");
        area = name;
        if (!knownAreas.Contains(name)) knownAreas.Add(name);
        reader.setArea(name);
        updateIntent();
    }

    /// <summary>What the objective asks, given the area: slay → red marks; travel → unused arches;
    /// "… in the &lt;area&gt;" while there → stay; travelling elsewhere → leave this area.</summary>
    void updateIntent()
    {
        Intent intent = objective?.intent(area, objectives.timedRun) ?? Intent.none;
        Intent before = reader.intent;
        reader.intent = intent;
        if (intent == before) return;
        Log.log($"Intent: slay {intent.slay} travel {intent.travel} stay {intent.stayIn ?? "-"} leave {intent.leave ?? "-"} to {intent.destination ?? "-"} timed {intent.timed}");
        // Said with the next lead, which knows whether the destination is somewhere already
        // walked through (then "Leading to …" says it) or somewhere still to find.
        if (isOn && intent.leave is string here && before.leave != here) leaveToSay = here; else leaveToSay = null;
        if (intent.leave == null) ledToArea = false;
    }

    /// <summary>Reads out what the map knows: healing wells, arches, the marked spot, marked
    /// enemies — each with its direction and distance from the player. What a sighted
    /// player gets from opening the full map.</summary>
    public void describeMap(List<string>? areas = null)
    {
        areas ??= new();
        if (!isOn) { tell("Guide is off.", "Basso"); return; }
        // Never re-announce an objective kind within five minutes; a description is enough.
        // The panel's list, less what the fast recogniser makes of it ("Ho]lowCa rns").
        // The panel's names, corrected to the tracker's spelling where they are close
        // ("Ziggurai Distrirt", 07:25 run); the rest only if they read like names.
        var names = new List<string>();
        foreach (var read in areas)
        {
            var known = knownAreas.FirstOrDefault(k => Objective.alike(k, read));
            if (known != null) { names.Add(known); continue; }
            if (MapScreen.looksLikeName(read)) names.Add(read);
        }
        bool unexplored = names.Any(n => n.ToLowerInvariant().StartsWith("unexplored", StringComparison.Ordinal));
        var listed = names.Where(n => !n.ToLowerInvariant().StartsWith("unexplored", StringComparison.Ordinal)).ToList();
        string prefix = "";
        if (listed.Count > 0)
        {
            prefix = "Map. Areas: " + string.Join(", ", listed) + (unexplored ? ", and unexplored areas. " : ". ");
        }
        else if (unexplored)
        {
            prefix = "Map. Unexplored areas remain. ";
        }
        reader.describe(text => tell(prefix + text, null));
    }

    /// <summary>Reads out the objective, counts included.</summary>
    public void sayObjective()
    {
        if (!isOn) { tell("Guide is off.", "Basso"); return; }
        // The watcher's copy is the best spelling so far and carries the count.
        var (mirror, count) = objectives.current;
        if ((mirror ?? this.objective) is not { } objective) { tell("Can't read the objective.", "Basso"); return; }
        tell($"{objective.place}. {spoken(objective, count ?? objective.count)}.", null);
    }

    /// <summary>Says where the beacon is leading, for when the tick alone is not enough.</summary>
    public void sayWhere()
    {
        if (!isOn) { tell("Guide is off.", "Basso"); return; }
        if (latest is not { } snapshot || !snapshot.legible) { tell("Can't read the map.", "Basso"); return; }
        if (snapshot.guidance is not { } g) { tell("No unexplored openings in sight.", "Pop"); return; }
        string howFar = Guide.howFar(g.distance);
        string also = alsoUnexplored(g.others);
        if (g.toSpot)
        {
            tell($"Marked spot {Compass.word(g.bearing)}, {howFar}.{also}", null);
        }
        else if (g.toArea)
        {
            string place = reader.intent.destination ?? "The area";
            tell($"{place} {Compass.word(g.bearing)}, {howFar}.{also}", null);
        }
        else if (g.toWell)
        {
            tell($"Unexplored ground by the healing well, {Compass.word(g.bearing)}, {howFar}.", null);
        }
        else if (g.toArch)
        {
            tell($"Arch {Compass.word(g.bearing)}, {howFar}.", null);
        }
        else if (g.toMark)
        {
            string more = g.marks > 1 ? $" {g.marks} marked." : "";
            tell($"Marked enemy {Compass.word(g.bearing)}, {howFar}.{more}", null);
        }
        else if (snapshot.toBeacon)
        {
            tell($"{snapshot.timeTarget} {Compass.word(snapshot.markerBearing ?? g.bearing)}, {howFar}. It buys time.{also}", null);
        }
        else if (g.markerInView)
        {
            tell($"Objective marker {Compass.word(g.bearing)}, {howFar}.{also}", null);
        }
        else if (g.beeline)
        {
            tell($"Straight on {Compass.word(g.bearing)}, towards the objective marker.{also}", null);
        }
        else if (g.toMarker && snapshot.markerBearing is double b)
        {
            tell($"Opening {Compass.word(g.bearing)}, {howFar}, towards the objective marker {Compass.word(b)}.{also}", null);
        }
        else
        {
            tell($"Opening {Compass.word(g.bearing)}, {howFar}.{also}", null);
        }
    }

    /// <summary>The other unexplored openings as words: the nearest in each direction, nearest
    /// first, four at most — "north, close; east, further off".</summary>
    public static List<string> directions(List<(double bearing, double distance)> openings)
    {
        var nearest = new Dictionary<string, double>();
        foreach (var o in openings)
        {
            string word = Compass.word(o.bearing);
            nearest[word] = Math.Min(nearest.TryGetValue(word, out var have) ? have : double.PositiveInfinity, o.distance);
        }
        return nearest.OrderBy(kv => kv.Value).Take(4).Select(kv => $"{kv.Key}, {howFar(kv.Value)}").ToList();
    }

    public static string alsoUnexplored(List<(double bearing, double distance)> openings)
    {
        var list = directions(openings);
        return list.Count == 0 ? "" : " Also unexplored: " + string.Join("; ", list) + ".";
    }

    void handle(Snapshot snapshot)
    {
        if (!isOn) return;
        latest = snapshot;
        var now = DateTime.UtcNow;

        if (!snapshot.legible)
        {
            beacon.silence();
            // A menu or banner over the screen: the tracker is not to be trusted either, and
            // the full map may be what is up — the map-screen watcher looks while this lasts.
            objectives.mapVisible = false;
            mapScreen.armed = true;
            if (!lostAnnounced && (now - lastLegible).TotalSeconds > lostAfter)
            {
                lostAnnounced = true;
                if (everLegible)
                {
                    // Said to nobody: once the minimap has been read, an unreadable one is
                    // almost always a menu the owner opened themselves (the 26 Sep run: the
                    // inventory for two and a half minutes) — they know. Logged only.
                    status = "Minimap covered.";
                    Log.log($"Minimap unreadable for {(int)lostAfter} s — a menu, probably");
                }
                else
                {
                    status = "Can't see the minimap. The guide reads the minimap, not the Map Overlay.";
                    tell("Can't see the minimap. The guide needs Map Display set to Minimap.", "Basso");
                }
            }
            logOccasionally(snapshot);
            return;
        }
        lastLegible = now;
        everLegible = true;
        objectives.mapVisible = true;
        mapScreen.disarm();
        if (lostAnnounced)
        {
            lostAnnounced = false;
            status = "Guide on.";
            Log.log("Map readable again");
        }
        if (snapshot.newMap) { mapBegan = now; saidAtMarker = false; tell("New map.", "Pop"); }
        // A timer not read for a while is gone (the run is over, or the badge is covered):
        // forget it, so nothing is said or routed on a stale count.
        if (lastTimer != null && freshTimer == null) { Log.log("Timer: stale, forgotten"); lastTimer = null; reader.timeLeft = null; }
        // A healing well or an arch coming into view is what a sighted player would notice
        // on the minimap: said once, when first confirmed.
        foreach (var (kind, bearing, distance) in snapshot.newLandmarks)
        {
            // The same icon can be confirmed twice when the map drifts a little over a long
            // run (the 26 Sep log said "Arch, south-east" four times): one of a kind within
            // `announceGap` seconds is said once.
            if (announcedLandmarks.TryGetValue(kind, out var last) && (now - last).TotalSeconds < announceGap) continue;
            announcedLandmarks[kind] = now;
            tell($"{kind.rawValue}, {Compass.word(bearing)}, {howFar(distance)}.", "Glass");
        }

        if (reader.leadToSpot)
        {
            if (snapshot.guidance is { } gs && gs.toSpot)
            {
                spotRouteMissingSince = null;
                if (gs.distance < arrived)
                {
                    reader.leadToSpot = false;
                    tell("Back at the marked spot.", "Glass");
                }
            }
            else if (spotRouteMissingSince == null)
            {
                spotRouteMissingSince = now;
            }
            else if (spotRouteMissingSince is DateTime since && (now - since).TotalSeconds > 3)
            {
                spotRouteMissingSince = DateTime.MaxValue;   // said once
                tell("No way back to the spot on the map yet. Exploring.", "Basso");
            }
        }

        if (snapshot.guidance is { } g)
        {
            noOpeningSince = null;
            lastGuidance = now;
            // A dead end is the navigator saying the opening it led to has closed, with the
            // route to it short — not merely a change of target, which is what the first
            // version tested and why it said "dead end" to a player standing still.
            if (leadingTo is { } previous && previous.epoch == g.mapEpoch
                && g.previousClosed
                && previous.distance < deadEndNear
                && (now - lastDeadEnd).TotalSeconds > 8
                && (now - mapBegan).TotalSeconds > 6)   // not while a new floor's map is settling (28 Sep: one 2.5 s in)
            {
                lastDeadEnd = now;
                // Haptic cue here on the Mac: haptics.deadEnd().
                tell($"Dead end. Next opening {Compass.word(g.bearing)}, {howFar(g.distance)}.", "Pop");
            }
            else if (noOpeningAnnounced)
            {
                tell($"Opening {Compass.word(g.bearing)}, {howFar(g.distance)}.", "Pop");
            }
            leadingTo = (g.target, g.distance, g.mapEpoch);
            // Say when the lead changes kind — to marked enemies, or back to exploring —
            // at most every 10 s, so a mark flickering at the edge of the map is not a chatter.
            // "Marked enemies" when the lead turns to them after 20 s without; nothing when it
            // turns back — marks come and go as enemies move in and out of view, and the
            // "no marked enemies left" line was said sixteen times in one run.
            if (g.toMark && !ledToMark && (now - lastMarkLead).TotalSeconds > 20)
            {
                tell($"Marked enemies {Compass.word(g.bearing)}.", "Glass");
            }
            if (g.toMark) lastMarkLead = now;
            ledToMark = g.toMark;
            if (g.toArea && !ledToArea)
            {
                string place = reader.intent.destination ?? "the area";
                tell($"Leading to {place}, {Compass.word(g.bearing)}, {howFar(g.distance)}.", "Glass");
                leaveToSay = null;
            }
            ledToArea = g.toArea;
            // The objective marker pinned to the minimap's edge: the route now heads its way.
            if (g.toMarker && !ledToMarker && snapshot.markerBearing is double b && (now - lastMarkerSaid).TotalSeconds > markerGap)
            {
                lastMarkerSaid = now;
                tell($"Objective marker {Compass.word(b)}. Heading for it.", "Glass");
            }
            if (snapshot.toBeacon && !ledToBeacon && (now - lastMarkerSaid).TotalSeconds > timeTargetGap)
            {
                lastMarkerSaid = now;
                bool @short = (freshTimer ?? 999) <= LiveReader.shortTime;
                string verb = snapshot.timeTarget == "Beacon" ? "Light it for time." : "Kill it for time.";
                tell((@short ? "Short on time. " : "") + $"{snapshot.timeTarget} {Compass.word(snapshot.markerBearing ?? g.bearing)}, {howFar(g.distance)}. {verb}", "Glass");
            }
            else if (g.markerInView && !markerWasInView && !snapshot.toBeacon && (now - lastMarkerSaid).TotalSeconds > markerGap)
            {
                lastMarkerSaid = now;
                tell($"Objective marker on the map, {Compass.word(g.bearing)}, {howFar(g.distance)}.", "Glass");
            }
            // The boss room on the map with the clock nearly out: say where it is every ten
            // seconds, so a fight can be broken off for it (08:15 run: out of time 40 px
            // from the door, fighting an event's spirits).
            if (g.markerInView && !snapshot.toBeacon && freshTimer is int left && left <= 30
                && (now - lastMarkerSaid).TotalSeconds >= 10)
            {
                lastMarkerSaid = now;
                tell($"Objective {Compass.word(g.bearing)}, {howFar(g.distance)}. {left} seconds.", null);
            }
            // A fight that is not paying: clock short, standing still among red marks, and the
            // timer has not gone up for a while. Run 1 of 28 Sep lost floor 2 to an Executioner
            // elite fought from 67 s down to 10 with the boss marker pinned east the whole time.
            else if (reader.intent.timed && freshTimer is int t && t <= LiveReader.shortTime
                && snapshot.heading == null && g.marks > 0
                && (now - lastTimerRise).TotalSeconds >= notPayingAfter
                && (now - lastNotPaying).TotalSeconds >= notPayingGap)
            {
                lastNotPaying = now;
                var towards = snapshot.markerBearing ?? g.bearing;
                tell($"This fight is not paying. Objective {Compass.word(towards)}. {t} seconds.", null);
            }
            ledToBeacon = snapshot.toBeacon;
            markerWasInView = g.markerInView;
            ledToMarker = g.toMarker;
            if (leaveToSay is string here)
            {
                leaveToSay = null;
                // With the objective marker showing, the marker is the answer, not the area.
                if (!g.toMarker) tell($"Looking outside {here}.", "Glass");
            }
            if (g.toArch && !ledToArch)
            {
                tell($"Leading to an arch {Compass.word(g.bearing)}. It may be the way in.", "Glass");
            }
            ledToArch = g.toArch;
            if (g.toWell && !ledToWell)
            {
                tell($"Leading to unexplored ground by the healing well, {Compass.word(g.bearing)}. The way on is usually near one.", "Glass");
            }
            ledToWell = g.toWell;
            if (noOpeningAnnounced) { noOpeningAnnounced = false; status = "Guide on."; }
            // How well the character faces the route: from the minimap arrow when it can be
            // read (it turns with the stick even standing still), else the direction of travel.
            var accuracy = BeaconAccuracy.away;
            if ((snapshot.facing ?? snapshot.heading) is double facing)
            {
                double difference = Math.Abs(facing - g.bearing) % (2 * Math.PI);
                if (difference > Math.PI) difference = 2 * Math.PI - difference;
                accuracy = difference < onCourseAngle ? BeaconAccuracy.on : (difference < nearAngle ? BeaconAccuracy.near : BeaconAccuracy.away);
            }
            bool onCourse = accuracy == BeaconAccuracy.on;
            // The direction in words, whenever it settles on a new one.
            bool atMarker = g.markerInView && g.distance < markerArrived;
            if (atMarker)
            {
                if (!saidAtMarker) { saidAtMarker = true; Log.log($"At the objective marker ({(int)g.distance} px)"); tell("At the objective marker.", "Glass"); }
            }
            else if (g.distance > markerArrived * 2) saidAtMarker = false;
            string word = Compass.word(g.bearing);
            if (directionCandidate?.word != word) directionCandidate = (word, now);
            bool turned = true;
            if (spokenBearing is double lastBearing)
            {
                double d = Math.Abs(g.bearing - lastBearing) % (2 * Math.PI);
                if (d > Math.PI) d = 2 * Math.PI - d;
                turned = d >= directionTurn;
            }
            if (!atMarker && word != spokenDirection && turned && directionCandidate?.since is DateTime candidateSince
                && (now - candidateSince).TotalSeconds >= directionHold && (now - lastDirectionSaid).TotalSeconds >= directionGap)
            {
                spokenDirection = word;
                spokenBearing = g.bearing;
                lastDirectionSaid = now;
                tell(word[..1].ToUpperInvariant() + word[1..] + ".", null);
            }
            // Micro steering: see the properties above. Decided before the beacon points.
            // A red mark nearer than enemyNear is a fight, not a wall (29 Sep 2026: the native render
            // shows a mark on most frames of the Undercity, and "no marks at all" never steered).
            bool enemyClose = (snapshot.nearestMark ?? double.PositiveInfinity) < enemyNear;
            bool stalled = snapshot.heading == null && accuracy != BeaconAccuracy.away && !enemyClose && !atMarker && (now - mapBegan).TotalSeconds > 6;
            // Still, with a mark close: say so, because the beacon will not steer round it.
            if (snapshot.heading == null && enemyClose)
            {
                enemiesCloseSince ??= now;
                if ((now - enemiesCloseSince.Value).TotalSeconds >= enemiesAfter && (now - lastEnemiesSaid).TotalSeconds >= enemiesGap)
                {
                    lastEnemiesSaid = now;
                    Log.log($"Enemies close for {(int)(now - enemiesCloseSince.Value).TotalSeconds} s, nearest mark {(int)(snapshot.nearestMark ?? 0)} px");
                    tell("Enemies close.", null);
                }
            }
            else enemiesCloseSince = null;
            if (snapshot.heading != null)
            {
                if (movingSince == null) movingSince = now;
            }
            else
            {
                movingSince = null;
            }
            // Steer only with the facing held steady: in a fight the character turns.
            bool facingSteady = true;
            if (pushingFacing is double f0 && snapshot.facing is double f)
            {
                double d = Math.Abs(f - f0) % (2 * Math.PI);
                if (d > Math.PI) d = 2 * Math.PI - d;
                facingSteady = d < 25 * Math.PI / 180;
            }
            if (stalled)
            {
                if (pushingSince == null) { pushingSince = now; pushingFacing = snapshot.facing; }
                if (facingSteady && pushingSince is DateTime since && (now - since).TotalSeconds >= steerAfter)
                {
                    if (steerBearing == null)
                    {
                        steerSince = now;
                        // Swift: `snapshot.sidestep.map { … } ?? steerSide` — without a sidestep the side is kept.
                        if (snapshot.sidestep is double side)
                        {
                            double d = (side - g.bearing) % (2 * Math.PI);
                            if (d > Math.PI) d -= 2 * Math.PI; if (d < -Math.PI) d += 2 * Math.PI;
                            steerSide = d >= 0 ? 1 : -1;
                        }
                    }
                    else if ((now - steerSince).TotalSeconds >= steerSwap)
                    {
                        steerSide = -steerSide;
                        steerSince = now;
                    }
                    steerBearing = g.bearing + steerSide * steerAngle;
                }
            }
            else if (movingSince is DateTime moving && (now - moving).TotalSeconds >= steerRelease)
            {
                steerBearing = null;
            }
            if (!silentTest)
            {
                if (atMarker) beacon.silence();
                else beacon.point(steerBearing ?? g.bearing, steerBearing == null ? accuracy : BeaconAccuracy.near);
                // On course: one glide on arriving there, then nothing — the double tick is
                // already saying it. Off course: a tap a second towards the route.
                // Haptic cues here on the Mac: haptics.onCourse() once on arriving on course, else haptics.direction(bearing: g.bearing).
            }
            wasOnCourse = onCourse;
            // Blocked: still, facing within 45° of the route, no enemies marked close by.
            // …and not in the first seconds of a map, standing at the entrance (09:36 run).
            bool pushing = stalled;
            // …and facing steadily: in a fight the character spins, and that is not a wall.
            bool steady = true;
            if (pushingFacing is double pf0 && snapshot.facing is double pf)
            {
                double d = Math.Abs(pf - pf0) % (2 * Math.PI);
                if (d > Math.PI) d = 2 * Math.PI - d;
                steady = d < 25 * Math.PI / 180;
            }
            if (pushing && steady)
            {
                if (pushingSince is DateTime since && (now - since).TotalSeconds >= blockedAfter
                    && (now - lastBlockedSaid).TotalSeconds >= blockedRepeat)
                {
                    lastBlockedSaid = now;
                    string steering = steerBearing is double sb ? Compass.word(sb) : "-";
                    Log.log($"Blocked for {(int)(now - since).TotalSeconds} s, steering {steering}");
                    if (speakBlocked)
                    {
                        if (snapshot.sidestep is double side)
                        {
                            tell($"Blocked. Try {Compass.word(side)}.", null);
                        }
                        else
                        {
                            tell("Blocked. Step back and try again.", null);
                        }
                    }
                }
            }
            else if (!pushing)
            {
                pushingSince = null;
                pushingFacing = null;
            }
        }
        else
        {
            steerBearing = null;
            pushingSince = null;
            if ((now - lastGuidance).TotalSeconds > holdGap) beacon.silence();
            if (noOpeningSince == null) noOpeningSince = now;
            if (!noOpeningAnnounced && noOpeningSince is DateTime since && (now - since).TotalSeconds > noOpeningAfter)
            {
                noOpeningAnnounced = true;
                status = "No unexplored openings in sight.";
                if ((now - lastNoOpening).TotalSeconds > noOpeningRepeat)
                {
                    lastNoOpening = now;
                    bool wasNear = (leadingTo?.distance ?? double.PositiveInfinity) < deadEndNear;
                    // Haptic cue here on the Mac: haptics.deadEnd() when wasNear.
                    tell(wasNear ? "Dead end. No other openings in sight." : "No unexplored openings in sight.", "Pop");
                }
                leadingTo = null;
            }
        }
        logOccasionally(snapshot);
    }

    /// <summary>One line a second: enough to reconstruct what the guide did, not a flood.</summary>
    void logOccasionally(Snapshot s)
    {
        var now = DateTime.UtcNow;
        if ((now - lastLog).TotalSeconds < 1) return;
        lastLog = now;
        string heading = s.heading is double h ? Compass.word(h) : "still";
        if (s.guidance is { } g)
        {
            // What the lead is, not just where: the 28 Sep runs could only be read by inference.
            string kind = g.toSpot ? "spot" : s.toBeacon ? (s.timeTarget == "Beacon" ? "beacon" : "pack")
                : g.markerInView ? "marker on map" : g.toMarker ? (g.beeline ? "marker beeline" : "opening to marker")
                : g.toMark ? "mark" : g.toArea ? "area" : g.toWell ? "well" : g.toArch ? "arch" : "opening";
            Log.log(string.Format(CultureInfo.InvariantCulture, "Guide: {0:F0} ms, contrast {1:F1}, moving {2}, lead {3} ({4:F0}°) {5:F0} px, {6} openings, to {7}{8}",
                s.milliseconds, s.contrast, heading, Compass.word(g.bearing), g.bearing * 180 / Math.PI, g.distance, g.openings, kind, g.marks > 0 ? $", {g.marks} marks" : ""));
        }
        else
        {
            Log.log(string.Format(CultureInfo.InvariantCulture, "Guide: {0:F0} ms, {1}, contrast {2:F1}, moving {3}", s.milliseconds,
                s.legible ? "no opening" : "illegible", s.contrast, heading));
        }
    }

    /// <summary>The sound plays only when VoiceOver is off — it is the channel for a sighted player.
    /// With VoiceOver on, speech is the whole message: a chime before every line was heard
    /// as an error bonk, then as a chime that "shouldn't be there" (the owner, 26 Sep).</summary>
    // PORT NOTE: the named sounds (Pop, Basso, Glass, Tink, Bottle) were macOS system sounds played
    // when VoiceOver was off; sounds for sighted players are not yet done on Windows. The `sound`
    // parameter is kept so every call site reads as the Swift does, and `speech.say` decides for
    // itself whether a screen reader is running (`speech.screenReaderRunning`).
    void tell(string text, string? sound)
    {
        Log.log("Say: " + text);
        if (silentTest) return;
        // if (sound != null && !speech.screenReaderRunning) play the sound — not yet on Windows.
        speech.say(text);
    }

    public static string howFar(double distance)
    {
        return distance < 120 ? "close" : (distance < 250 ? "a little way" : (distance < 600 ? "further off" : "a long way back"));
    }

    // PORT NOTE: `stamp()` (the timestamp for the saved map PNG) went with `reader.saveMap`.
}

public static class DoubleClamp
{
    public static double clamped(this double value, double low, double high) => Math.Max(low, Math.Min(high, value));
}
