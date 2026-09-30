// The shell's state and its worker loop, shared by the window, the hot keys and Main.
//
// One worker thread does everything in order, ten times a second: grab the minimap's
// rectangle (plus a margin for edge-pinned icons) off the screen, hand it to the reader,
// let the guide speak and point the beacon; once a second, grab the HUD column and read its
// text; while the minimap is covered, grab the whole window at half size and look for the
// full map. Hot keys and the window's buttons queue actions onto the same thread, so the
// ported guide code never needs a lock. The log goes to %LOCALAPPDATA%\SanctuarySonar\logs,
// to the console if there is one, and to the window's "Recent log" box.
using System.Collections.Concurrent;
using System.Diagnostics;
using SanctuarySonar.Cartography;

namespace SanctuarySonar.Shell;

public sealed class Shell
{
    public readonly string dataDir, logPath;
    public readonly Speech speech;
    public readonly BeaconOut beacon;
    public readonly LiveReader reader;
    public readonly ObjectiveWatcher objectives;
    public readonly MapScreenWatcher mapScreen;
    public readonly Guide guide;
    readonly HudTextReader ocr;
    readonly ScreenGrabber grabber;
    readonly ConcurrentQueue<Action> queue = new();
    readonly StreamWriter logFile;
    readonly object logLock = new();
    int picturesLeft, hudPicturesLeft, textLogsLeft = 10;
    readonly int picturesWanted;

    /// <summary>Part of the game window's title to look for ("Diablo IV" unless changed).
    /// Remembered in the settings file.</summary>
    public string windowPart
    {
        get => _windowPart;
        set { _windowPart = value; Settings.set("window", value); }
    }
    string _windowPart;

    /// <summary>The window being read while the guide is on, else null.</summary>
    public GameWindow? window { get; private set; }

    /// <summary>Whether beacons are led to for time. Off by default: they need lighting by hand.</summary>
    public bool leadToBeacons
    {
        get => LiveReader.leadToBeacons;
        set { LiveReader.leadToBeacons = value; Settings.set("leadToBeacons", value ? 1 : 0); Log.log($"Lead to beacons: {(value ? "on" : "off")}"); }
    }

    // The recorder: the game window at 10 fps, driven from the worker loop like the guide.
    public readonly Recorder recorder = new();
    readonly ScreenGrabber recordGrabber = new(bottomUp: true);   // Media Foundation reads BGRA bottom-up
    GameWindow? recordWindow;
    double recordStart, nextRecordFrame, nextSpaceCheck, nextStillNote;
    string captureSource = "";
    public string recordingsDir => Path.Combine(dataDir, "recordings");

    /// <summary>Every log line, already time-stamped, for the window's log box. Called on the
    /// thread that logged; the window marshals.</summary>
    public Action<string>? onLog;

    /// <summary>The lines logged before the window opened, so its log box can start with them.</summary>
    public readonly List<string> linesBeforeWindow = new();

    public Shell(int pictures)
    {
        picturesWanted = pictures; picturesLeft = pictures; hudPicturesLeft = Math.Min(pictures, 2);
        dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SanctuarySonar");
        Directory.CreateDirectory(Path.Combine(dataDir, "logs"));
        logPath = Path.Combine(dataDir, "logs", $"sonar-{DateTime.Now:yyyy-MM-dd-HHmmss}.log");
        logFile = new StreamWriter(logPath, append: false) { AutoFlush = true };
        Log.sink = text =>
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {text}";
            lock (logLock)
            {
                Console.WriteLine(line); logFile.WriteLine(line);
                if (onLog == null && linesBeforeWindow.Count < 100) linesBeforeWindow.Add(line);
            }
            try { onLog?.Invoke(line); } catch { }
        };
        Settings.path = Path.Combine(dataDir, "settings.txt");
        Settings.load();
        _windowPart = Settings.getString("window", "Diablo IV");
        LiveReader.leadToBeacons = Settings.getDouble("leadToBeacons", 0) != 0;
        Log.log($"Sanctuary Sonar {typeof(Guide).Assembly.GetName().Version} on Windows; log {logPath}");
        Log.log($"Lead to beacons: {(LiveReader.leadToBeacons ? "on" : "off")}");

        speech = new Speech();
        beacon = new BeaconOut();
        reader = new LiveReader();
        objectives = new ObjectiveWatcher();
        mapScreen = new MapScreenWatcher();
        guide = new Guide(speech, beacon, reader, objectives, mapScreen);
        ocr = new HudTextReader();
        grabber = new ScreenGrabber();
    }

    /// <summary>Runs `action` on the worker thread, in order with everything else.</summary>
    public void post(Action action) => queue.Enqueue(action);

    /// <summary>What the window shows as the status: the guide's word plus the window it reads.</summary>
    public string status
    {
        get
        {
            var s = guide.isOn && window != null ? $"Guide on, reading {window.title}." : guide.status;
            if (recorder.isRecording) s += $" Recording, {(int)(DateTime.Now - recorder.startedAt).TotalMinutes} min.";
            return s;
        }
    }

    /// <summary>Start or stop recording the game window (Ctrl-Shift-Alt-R / the window's button).</summary>
    public void toggleRecording()
    {
        if (recorder.isRecording) { stopRecording(null); return; }
        var target = guide.isOn && window != null ? window : GameWindow.find(windowPart);
        if (target == null || !target.refresh())
        {
            Log.log($"Record: no window with \"{windowPart}\" in its title.");
            speech.say($"No window called {windowPart} to record. Is the game running?");
            return;
        }
        var free = Recorder.freeBytes(dataDir);
        if (free < Recorder.refuseBelow)
        {
            Log.log($"Record: only {free >> 30} GB free; not starting.");
            speech.say($"Not enough disk space to record. {free >> 30} gigabytes free.");
            return;
        }
        var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
        var name = $"run-{stamp}";
        int w = target.clientWidth & ~1, h = target.clientHeight & ~1;
        if (!recorder.start(Path.Combine(recordingsDir, name), name, w, h, $"Window: {target}"))
        {
            speech.say("Recording could not start. See the log.");
            return;
        }
        recordWindow = target;
        var now = clock.Elapsed.TotalSeconds;
        recordStart = now; nextRecordFrame = now; nextSpaceCheck = now + 15; nextStillNote = now + 900;
        speech.say("Recording.");
    }

    void stopRecording(string? why)
    {
        if (!recorder.isRecording) return;
        var length = recorder.stop();
        var folder = recorder.folder!;
        try { File.Copy(logPath, Path.Combine(folder, recorder.baseName + ".log"), overwrite: true); }
        catch (Exception e) { Log.log($"Could not copy the log beside the recording: {e.Message}"); }
        try { if (Settings.path != null && File.Exists(Settings.path)) File.Copy(Settings.path, Path.Combine(folder, "settings.txt"), overwrite: true); } catch { }
        recordWindow = null;
        Log.log($"Recording saved in {folder}");
        speech.say($"{why ?? "Recording stopped"}, {Math.Max(1, (int)Math.Round(length.TotalMinutes))} minutes.");
    }

    // Map points: the world map's icons as a list to step through (L / K), point at and click
    // (J twice). The only thing that ever goes to the game is the pointer and that click.
    public List<MapPoint> mapPoints { get; private set; } = new();
    public int mapIndex { get; private set; } = -1;
    public int mapWidth { get; private set; } = 2560;
    DateTime pointedAt = DateTime.MinValue;
    int pointedIndex = -1;
    public static readonly double clickWithin = 15;
    public Action? onMapPoints;
    readonly ScreenGrabber mapGrabber = new();

    GameWindow? gameWindowNow()
    {
        var w = guide.isOn && window != null ? window : GameWindow.find(windowPart);
        if (w == null || !w.refresh()) { speech.say($"No window called {windowPart}. Is the game running?"); return null; }
        return w;
    }

    /// <summary>Reads the map screen's icons and says what was found and the nearest one.</summary>
    public void scanMap()
    {
        var w = gameWindowNow(); if (w == null) return;
        mapGrabber.beginFrame(w);
        int W = w.clientWidth & ~1, H = w.clientHeight & ~1;
        var px = mapGrabber.grab(w.clientLeft, w.clientTop, W, H);
        if (px == null) { speech.say("Could not capture the game."); return; }
        var began = Stopwatch.StartNew();
        var (points, player, diamond) = MapIcons.find(px, W, H);
        mapPoints = points; mapWidth = W; mapIndex = points.Count > 0 ? 0 : -1; pointedIndex = -1;
        Log.log($"Map points: {points.Count} in {began.ElapsedMilliseconds} ms; player {(diamond ? "diamond" : "centre")} at {player.x:0},{player.y:0}; " + string.Join("; ", points.Take(40)));
        onMapPoints?.Invoke();
        if (points.Count == 0) { speech.say("No map points found. Is the map open?"); return; }
        var kinds = points.GroupBy(p => p.kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : "s")}");
        speech.say($"{points.Count} points: {string.Join(", ", kinds)}. {sayPoint()}");
    }

    string sayPoint() => mapIndex < 0 ? "" : $"{mapIndex + 1} of {mapPoints.Count}. {MapIcons.describe(mapPoints[mapIndex], mapWidth)}";

    public void nextPoint()
    {
        if (mapIndex < 0) { scanMap(); return; }
        mapIndex = (mapIndex + 1) % mapPoints.Count;
        onMapPoints?.Invoke(); speech.say(sayPoint());
    }

    public void previousPoint()
    {
        if (mapIndex < 0) { scanMap(); return; }
        mapIndex = (mapIndex - 1 + mapPoints.Count) % mapPoints.Count;
        onMapPoints?.Invoke(); speech.say(sayPoint());
    }

    /// <summary>From the window's list.</summary>
    public void selectPoint(int index) { if (index >= 0 && index < mapPoints.Count) mapIndex = index; }

    /// <summary>First press: put the pointer on the chosen point and read the game's tooltip.
    /// Second press on the same point within `clickWithin` seconds: click it.</summary>
    public void goToPoint(bool clickNow = false)
    {
        if (mapIndex < 0) { speech.say("No point chosen. Scan the map first."); return; }
        var w = gameWindowNow(); if (w == null) return;
        var p = mapPoints[mapIndex];
        if (clickNow || (pointedIndex == mapIndex && (DateTime.UtcNow - pointedAt).TotalSeconds < clickWithin))
        {
            if (!Pointer.isInFront(w.handle)) { Pointer.bringToFront(w.handle); Thread.Sleep(150); }
            Pointer.moveTo(w.clientLeft + p.x, w.clientTop + p.y);
            Thread.Sleep(80);
            Log.log($"Click: {p} ({p.name ?? "unnamed"})");
            Pointer.click();
            pointedIndex = -1;
            speech.say("Clicked.");
            return;
        }
        if (!Pointer.isInFront(w.handle)) { var ok = Pointer.bringToFront(w.handle); Log.log($"Bring the game to the front: {ok}"); Thread.Sleep(150); }
        Pointer.moveTo(w.clientLeft + p.x, w.clientTop + p.y);
        Log.log($"Pointer on {p}");
        Thread.Sleep(650);   // the tooltip fades in
        var tip = readTooltip(w, p);
        pointedAt = DateTime.UtcNow; pointedIndex = mapIndex;
        if (tip != null) p.name = tip;
        onMapPoints?.Invoke();
        speech.say($"{tip ?? p.kind}. Press again to click.");
    }

    /// <summary>The game's tooltip for the icon under the pointer: its lines, top to bottom, with
    /// the map's own region names (all capitals) left out. Null if nothing was read.</summary>
    string? readTooltip(GameWindow w, MapPoint p)
    {
        if (!ocr.available) return null;
        int W = w.clientWidth, H = w.clientHeight;
        int rx = Math.Max(0, p.x - 560), ry = Math.Max(0, p.y - 700);
        int rw = Math.Min(W, p.x + 560) - rx, rh = Math.Min(H, p.y + 120) - ry;
        rw &= ~1; rh &= ~1;
        mapGrabber.beginFrame(w);
        var px = mapGrabber.grab(w.clientLeft + rx, w.clientTop + ry, rw, rh);
        if (px == null) return null;
        var lines = ocr.placedLinesXY(px, rw, rh).OrderBy(l => l.y).ToList();
        Log.log($"Tooltip text: " + string.Join(" | ", lines.Select(l => l.text)));
        var kept = lines.Select(l => l.text.Trim())
            .Where(t => t.Count(char.IsLetter) >= 3)
            .Where(t => t.Any(char.IsLower))   // region names are capitals
            .Take(4).ToList();
        return kept.Count == 0 ? null : string.Join(". ", kept);
    }

    /// <summary>Notes the moment in the recording's .txt (Ctrl-Shift-Alt-M).</summary>
    public void markRecording()
    {
        if (!recorder.isRecording) { speech.say("Not recording."); return; }
        var t = TimeSpan.FromSeconds(clock.Elapsed.TotalSeconds - recordStart);
        recorder.mark(t);
        Log.log($"Recording mark at {t:m\\:ss}");
        speech.say("Marked.");
    }

    void recordFrame(double now)
    {
        if (recordWindow == null || !recordWindow.refresh()) { Log.log("The recorded window went away."); stopRecording("The window went away. Recording stopped"); return; }
        int w = recordWindow.clientWidth & ~1, h = recordWindow.clientHeight & ~1;
        if (w != recorder.width || h != recorder.height) return;   // resized: skip until it is back
        recordGrabber.beginFrame(recordWindow);   // the game's own picture, not whatever is over it
        var bgra = recordGrabber.grab(recordWindow.clientLeft, recordWindow.clientTop, w, h);
        if (bgra == null) return;
        recorder.push(bgra, TimeSpan.FromSeconds(now - recordStart));
        if (now >= nextSpaceCheck)
        {
            nextSpaceCheck = now + 15;
            if (Recorder.freeBytes(dataDir) < Recorder.stopBelow) { Log.log("Disk nearly full."); stopRecording("Disk nearly full. Recording stopped"); return; }
        }
        if (now >= nextStillNote)
        {
            nextStillNote = now + 900;
            speech.say($"Still recording, {(int)Math.Round((now - recordStart) / 60)} minutes.");
        }
    }

    public void toggleGuide()
    {
        if (guide.isOn) { guide.stop(); return; }
        window = GameWindow.find(windowPart);
        if (window == null)
        {
            Log.log($"No window with \"{windowPart}\" in its title. Windows: " + string.Join("; ", GameWindow.list().Select(w => w.title)));
            speech.say($"No window called {windowPart}. Is the game running?");
            return;
        }
        var mm = MinimapLayout.rect(window.clientWidth, window.clientHeight, 0);
        Log.log($"Guide on: {window}; minimap {mm.width}x{mm.height} at {mm.x},{mm.y}");
        picturesLeft = Math.Max(picturesLeft, Math.Min(picturesWanted, 3)); hudPicturesLeft = Math.Max(hudPicturesLeft, Math.Min(picturesWanted, 2)); textLogsLeft = 10;
        objectives.reset();
        guide.start();
    }

    public void quit()
    {
        Log.log("Quit");
        if (recorder.isRecording) stopRecording("Quitting. Recording stopped");   // finish the file first
        Environment.Exit(0);
    }

    /// <summary>Registers every function as Control-Shift-Alt + key. The same functions are
    /// the window's buttons.</summary>
    public HotKeys makeHotKeys()
    {
        var hotKeys = new HotKeys(post);
        hotKeys.register("guide", 'G', toggleGuide);
        hotKeys.register("where", 'W', () => guide.sayWhere());
        hotKeys.register("describe", 'D', () => guide.describeMap(new List<string>()));
        hotKeys.register("objective", 'O', () => guide.sayObjective());
        hotKeys.register("mark spot", 'S', () => guide.markSpot());
        hotKeys.register("take me back", 'B', () => guide.takeMeBack());
        hotKeys.register("new map", 'N', () => guide.newMap());
        hotKeys.register("louder", 0xBB /* VK_OEM_PLUS */, () => guide.louder());
        hotKeys.register("quieter", 0xBD /* VK_OEM_MINUS */, () => guide.quieter());
        hotKeys.register("record", 'R', toggleRecording);
        hotKeys.register("mark", 'M', markRecording);
        hotKeys.register("map points", 'L', nextPoint);
        hotKeys.register("previous point", 'K', previousPoint);
        hotKeys.register("go to point", 'J', () => goToPoint());
        hotKeys.register("quit", 'Q', quit);
        return hotKeys;
    }

    /// <summary>Starts the worker loop on its own background thread.</summary>
    public void start()
    {
        var thread = new Thread(run) { IsBackground = true, Name = "worker" };
        thread.Start();
    }

    readonly Stopwatch clock = Stopwatch.StartNew();

    void run()
    {
        double startedAt = 0, nextFrame = 0, nextText = 0, nextMapCheck = 0, nextIllegibleLog = 0;
        bool wasOn = false;
        while (true)
        {
            while (queue.TryDequeue(out var action)) { try { action(); } catch (Exception e) { Log.log($"Action failed: {e}"); } }
            var now = clock.Elapsed.TotalSeconds;
            bool guiding = guide.isOn && window != null;
            if (!guiding && !recorder.isRecording) { wasOn = false; Thread.Sleep(20); continue; }
            // A recording frame is due ten times a second whether or not the guide is on.
            if (recorder.isRecording && now >= nextRecordFrame)
            {
                nextRecordFrame += 1.0 / Recorder.framesPerSecond;
                if (nextRecordFrame < now) nextRecordFrame = now;
                try { recordFrame(now); } catch (Exception e) { Log.log($"Recording frame failed: {e.Message}"); }
            }
            if (!guiding) { wasOn = false; Thread.Sleep(5); continue; }
            if (!wasOn) { wasOn = true; startedAt = now; nextFrame = now; nextText = now; }
            if (now < nextFrame) { Thread.Sleep(5); continue; }
            nextFrame += 1.0 / LiveReader.framesPerSecond;
            if (nextFrame < now) nextFrame = now;
            try { step(now, startedAt, ref nextText, ref nextMapCheck, ref nextIllegibleLog); }
            catch (Exception e) { Log.log($"Frame failed: {e}"); }
        }
    }

    void step(double now, double startedAt, ref double nextText, ref double nextMapCheck, ref double nextIllegibleLog)
    {
        if (window == null) return;
        if (!window.refresh())
        {
            Log.log("The game window went away.");
            speech.say("The game window went away. Guide off.");
            guide.stop();
            return;
        }
        int W = window.clientWidth, H = window.clientHeight;
        // The game window's own picture for this frame, so a window over it does not get read.
        grabber.beginFrame(window);
        if (grabber.source != captureSource) { captureSource = grabber.source; Log.log($"Capture: from the {captureSource}" + (captureSource == "screen" ? " (PrintWindow failed; anything on top of the game is read too)" : "")); }
        var box = MinimapLayout.rect(W, H, 0);
        var (outer, inset) = MinimapLayout.outer(box, W, H);
        var bgra = grabber.grab(window.clientLeft + outer.x, window.clientTop + outer.y, outer.width & ~1, outer.height & ~1);
        if (bgra == null) { Log.log("Capture failed"); return; }
        int fw = outer.width & ~1, fh = outer.height & ~1;
        var frame = new Frame
        {
            nv12 = ScreenGrabber.toNV12(bgra, fw, fh), width = fw, height = fh, rowBytes = fw,
            box = new Rect(inset.x, inset.y, Math.Min(box.width, fw - inset.x), Math.Min(box.height, fh - inset.y)),
            inset = inset, time = now - startedAt
        };
        // The first frames of each guide session as pictures beside the log, for checking what
        // the capture and the reader see (--pictures N writes N of them; default 3).
        if (picturesLeft > 0)
        {
            picturesLeft--;
            var stamp = $"{DateTime.Now:HHmmss}-{picturesLeft}";
            Bmp.write(Path.Combine(dataDir, "logs", $"frame-{stamp}.bmp"), bgra, fw, fh);
            Bmp.writeGray(Path.Combine(dataDir, "logs", $"luma-{stamp}.bmp"), frame.nv12, 0, fw, fw, fh);
            var r = MinimapReader.read(new Gray(frame.nv12, 0, fw, frame.box));
            Log.log($"Frame {stamp}: capture {fw}x{fh} at {outer.x},{outer.y}; box {frame.box.width}x{frame.box.height} at {frame.box.x},{frame.box.y}; reading: legible {r.isLegible}, contrast {r.contrast:F1}, floor {r.floorFraction:P0}, busy {r.busyFraction:F2}, arrow {(r.arrow is Pt ap ? $"({ap.x:0},{ap.y:0})" : "none")}, threshold {r.threshold}");
        }
        // While frames are rejected, say why every five seconds (the inventory's numbers are unknown).
        if (now >= nextIllegibleLog)
        {
            var r = MinimapReader.read(new Gray(frame.nv12, 0, fw, frame.box));
            if (!r.isLegible)
            {
                nextIllegibleLog = now + 5;
                Log.log($"Illegible: contrast {r.contrast:F1}, floor {r.floorFraction:P0}, busy {r.busyFraction:F2}, arrow {(r.arrow == null ? "none" : "found")}");
            }
        }
        reader.process(frame);

        // The tracker's text, once a second, only while the minimap is readable.
        if (now >= nextText && objectives.mapVisible && ocr.available)
        {
            nextText = now + 1;
            var column = TrackerLayout.hudRect(W, H, 0);
            var hud = grabber.grab(window.clientLeft + column.x, window.clientTop + column.y, column.width, column.height);
            if (hud != null)
            {
                if (hudPicturesLeft > 0) { hudPicturesLeft--; Bmp.write(Path.Combine(dataDir, "logs", $"hud-{DateTime.Now:HHmmss}.bmp"), hud, column.width, column.height); }
                var lines = ocr.placedLines(ScreenGrabber.grayBgra(hud, column.width, column.height), column.width, column.height);
                if (textLogsLeft > 0) { textLogsLeft--; Log.log($"HUD text {lines.Count} lines: " + string.Join(" | ", lines.Select(l => $"{l.y:F2} {l.text}"))); }
                objectives.update(lines);
            }
        }
        // The full map, once a second, only while the minimap is covered.
        if (mapScreen.armed && now >= nextMapCheck && ocr.available)
        {
            nextMapCheck = now + 1;
            var half = grabber.grab(window.clientLeft, window.clientTop, W & ~1, H & ~1, 2);
            if (half != null)
            {
                int hw = (W & ~1) / 2, hh = (H & ~1) / 2;
                mapScreen.check(ocr.placedLinesXY(half, hw, hh), ScreenGrabber.redTabFraction(half, hw, hh));
            }
        }
    }
}
