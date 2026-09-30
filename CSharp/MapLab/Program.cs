// MapLab (C#): runs the minimap reader and the navigator over a recording and prints what it
// saw — the same lines as the Swift MapLab, so the two can be diffed. Frames are decoded by
// ffmpeg into NV12 (the plane layout of Apple's 420v: luma, then interleaved Cb/Cr at half
// size), frame times come from ffprobe, and the HUD's text lines come from a log the Swift
// tool wrote (MAPLAB_HUDLOG), since this tool has no OCR on a Mac.
//
//     MapLab <recording.mov> <output folder> [title bar pixels, default 57] [canvas, default 3000]
//
// Environment: MAPLAB_BUSY, MAPLAB_MARKS, MAPLAB_ARCHES, MAPLAB_SPOT, MAPLAB_INTENT, MAPLAB_UNTIL,
// MAPLAB_NOMARKER, MAPLAB_CANDIDATES_FROM, MAPLAB_CANDIDATES_STEP as in the Swift tool, and
// MAPLAB_HUD=<file> for the HUD log to replay (needed with MAPLAB_INTENT=1).
using System.Diagnostics;
using System.Globalization;
using SanctuarySonar.Cartography;
using AreaRule = SanctuarySonar.Cartography.Navigator.AreaRule;

var ci = CultureInfo.InvariantCulture;
string? Env(string name) => Environment.GetEnvironmentVariable(name);
double EnvDouble(string name, double fallback) => double.TryParse(Env(name), NumberStyles.Float, ci, out var v) ? v : fallback;

if (args.Length < 2) { Console.WriteLine("usage: MapLab <recording.mov> <output folder> [title bar pixels] [canvas]"); return 2; }
var videoPath = args[0];
var outPath = args[1];
var titleBar = args.Length > 2 && int.TryParse(args[2], out var tb) ? tb : 57;
var canvas = args.Length > 3 && int.TryParse(args[3], out var cv) ? cv : 3000;
const double interval = 0.2;
if (Env("MAPLAB_BUSY") is string busyText && double.TryParse(busyText, NumberStyles.Float, ci, out var busy)) MinimapReading.busyLimit = busy;
var leadToMarks = Env("MAPLAB_MARKS") == "1";
var leadToArches = Env("MAPLAB_ARCHES") == "1";
var spotTimes = (Env("MAPLAB_SPOT") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
    .Select(s => double.TryParse(s, NumberStyles.Float, ci, out var d) ? (double?)d : null).Where(d => d.HasValue).Select(d => d!.Value).ToList();
var spotMarked = false;
var followIntent = Env("MAPLAB_INTENT") == "1";
var leadToBeacons = Env("MAPLAB_BEACONS") == "1";
var until = EnvDouble("MAPLAB_UNTIL", double.PositiveInfinity);
var noMarker = Env("MAPLAB_NOMARKER") == "1";
Directory.CreateDirectory(outPath);

// The HUD log: "time\ty\ttext" per text line, "time\t-" for a read with none, grouped by time.
var hudLog = new Dictionary<string, List<(string text, double y)>>();
if (Env("MAPLAB_HUD") is string hudPath && File.Exists(hudPath))
{
    foreach (var line in File.ReadLines(hudPath))
    {
        var parts = line.Split('\t');
        if (parts.Length < 2) continue;
        if (!hudLog.TryGetValue(parts[0], out var list)) { list = new(); hudLog[parts[0]] = list; }
        if (parts.Length >= 3 && double.TryParse(parts[1], NumberStyles.Float, ci, out var y)) list.Add((parts[2], y));
    }
}
else if (followIntent) Console.Error.WriteLine("MAPLAB_INTENT=1 without MAPLAB_HUD=<file>: no text will be read");

// Probe the video: size, then every packet's presentation time (sorted = frame order).
string Probe(string entries) {
    var p = Process.Start(new ProcessStartInfo("ffprobe", $"-v error -select_streams v:0 -show_entries {entries} -of csv=p=0 \"{videoPath}\"") { RedirectStandardOutput = true })!;
    var text = p.StandardOutput.ReadToEnd(); p.WaitForExit(); return text;
}
var dims = Probe("stream=width,height").Trim().Split(',');
int width = int.Parse(dims[0]), height = int.Parse(dims[1]);
// Integer timestamps and the stream's time base, so each frame's time is the same double
// Swift gets from CMTime (value / timescale): ffprobe's pts_time is rounded to six decimals,
// and frames on the 1/600 s grid then fell a hair short of nextSample and were skipped
// differently (first divergence at 34.51 s on the 09:36 recording).
var timeBase = Probe("stream=time_base").Trim().Split('/');
double tbNum = double.Parse(timeBase[0], ci), tbDen = timeBase.Length > 1 ? double.Parse(timeBase[1], ci) : 1;
var ptsTicks = Probe("packet=pts").Split('\n', StringSplitOptions.RemoveEmptyEntries)
    .Select(s => long.TryParse(s.Trim().TrimEnd(','), NumberStyles.Integer, ci, out var t) ? t : long.MinValue).Where(t => t != long.MinValue).ToList();
ptsTicks.Sort();
var pts = ptsTicks.Select(t => tbNum == 1 ? t / tbDen : t * tbNum / tbDen).ToList();

// Only the HUD column is needed: crop from an even x so the chroma plane stays aligned.
int cropX = Math.Max(0, ((width - 760) / 2) * 2), cropW = width - cropX, cropH = height;
var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg", $"-v error -i \"{videoPath}\" -fps_mode passthrough -f rawvideo -pix_fmt nv12 -vf crop={cropW}:{cropH}:{cropX}:0 -")
    { RedirectStandardOutput = true, UseShellExecute = false })!;
var stream = ffmpeg.StandardOutput.BaseStream;
int lumaBytes = cropW * cropH, frameBytes = lumaBytes + cropW * cropH / 2;
var frame = new byte[frameBytes];
bool ReadFrame() {
    int got = 0;
    while (got < frameBytes) { int n = stream.Read(frame, got, frameBytes - got); if (n <= 0) return false; got += n; }
    return true;
}

var stitcher = new Stitcher(canvas);
var navigator = new Navigator();
double nextSample = 0, nextText = 0;
var hud = new HUDState();
var intent = new Intent(false, false, null, null);
if (double.TryParse(Env("MAPLAB_CANDIDATES_FROM"), NumberStyles.Float, ci, out var from))
{
    double last = -1;
    navigator.debug = text => {
        var step = EnvDouble("MAPLAB_CANDIDATES_STEP", 1);
        if (nextSample >= from && nextSample - last >= step) { last = nextSample; Console.WriteLine($"{nextSample,7:F2} s  CANDIDATES {text}"); }
    };
}
int samples = 0, placed = 0, illegible = 0;
Guidance? lastGuidance = null;
double? refusedSince = null;
int? floorSeen = null;
double settleUntil = -1.0;   // nothing stitched for 2.5 s after a floor change (30 Sep 2026)
double beaconSaid = -10, markerSaid = -10, objectiveSaid = -10;
var timeIcons = new List<(Pt, string)>();
Pt? lastTimeTarget = null;
var doneTargets = new List<Pt>();
double? markerBearing = null;
double markerSeen = -1;

static string F0(double x) => Math.Round(x, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture);
static string Signed3(int v) => ((v >= 0 ? "+" : "-") + Math.Abs(v).ToString(CultureInfo.InvariantCulture)).PadLeft(3);
static string Name(Landmark k) => k == Landmark.healingWell ? "Healing well" : "Arch";

int frameIndex = 0;
while (ReadFrame())
{
    if (frameIndex >= pts.Count) break;
    var time = pts[frameIndex++];
    if (time > until) break;
    if (time < nextSample) continue;
    nextSample = time + interval;

    var rectFull = MinimapLayout.rect(width, height, titleBar);
    var rect = new Rect(rectFull.x - cropX, rectFull.y, rectFull.width, rectFull.height);
    var gray = new Gray(frame, 0, cropW, rect);
    if (followIntent && time >= nextText && MinimapReader.read(gray).isLegible)
    {
        nextText = time + 1;
        // The Swift tool read the column here; we replay what it read at this time.
        var key = time.ToString("F2", ci);
        var lines = hudLog.TryGetValue(key, out var logged) ? logged : new List<(string text, double y)>();
        var changed = hud.update(lines);
        if (changed.area is string a) { stitcher.setArea(a); Console.WriteLine($"{time,7:F2} s  AREA {a}"); }
        if (changed.objective is Objective o) Console.WriteLine($"{time,7:F2} s  OBJECTIVE {o.text}");
        if (changed.count is int n) Console.WriteLine($"{time,7:F2} s  COUNT {n} ({hud.objective?.kind ?? ""})");
        if (changed.timer is int t) Console.WriteLine($"{time,7:F2} s  TIMER {t}");
        if (changed.floor is var f && f.HasValue)
        {
            Console.WriteLine($"{time,7:F2} s  FLOOR {f.Value.number} of {f.Value.of}{(floorSeen == null ? "" : " — new map")}");
            if (floorSeen != null) { stitcher.reset(); navigator.forget(); doneTargets.Clear(); lastTimeTarget = null; settleUntil = time + 2.5; }
            floorSeen = f.Value.number;
        }
        var newIntent = hud.objective?.intent(hud.area, hud.timedRun) ?? intent;
        if (!newIntent.Equals(intent))
        {
            intent = newIntent;
            Console.WriteLine($"{time,7:F2} s  INTENT slay {(intent.slay ? "yes" : "no")} travel {(intent.travel ? "yes" : "no")} stay {intent.stayIn ?? "-"} leave {intent.leave ?? "-"} to {intent.destination ?? "-"} timed {(intent.timed ? "yes" : "no")}");
        }
    }
    Pt[] marks = Array.Empty<Pt>();
    Pt? marker = null;
    {
        var (outerFull, inset) = MinimapLayout.outer(rectFull, width, height);
        var outer = new Rect(outerFull.x - cropX, outerFull.y, outerFull.width, outerFull.height);
        marks = RedMarks.find(frame, lumaBytes, cropW, rect);
        marker = ObjectiveMarker.find(frame, 0, cropW, frame, lumaBytes, cropW, outer, inset);
        if (intent.timed)
        {
            // Beacons only with MAPLAB_BEACONS=1, as the live guide's switch (off by default since 28 Sep 2026).
            timeIcons = (leadToBeacons ? BlueIcons.find(frame, 0, cropW, frame, lumaBytes, cropW, outer, inset).Select(p => (p, "beacon")) : Enumerable.Empty<(Pt, string)>())
                .Concat(OrangeIcons.find(frame, 0, cropW, frame, lumaBytes, cropW, outer, inset).Select(p => (p, "afflicted"))).ToList();
        }
        else timeIcons.Clear();
    }

    var reading = MinimapReader.read(gray);
    reading.marks = marks;
    if (!intent.timed) marker = null;
    if (marker is Pt m0 && reading.arrow is Pt arrow0 && time - objectiveSaid >= 5)
    {
        objectiveSaid = time;
        Console.WriteLine($"{time,7:F2} s  OBJECTIVE MARKER at ({F0(m0.x)},{F0(m0.y)}), {Compass.word(Math.Atan2(m0.x - arrow0.x, -(m0.y - arrow0.y)))} of the player");
    }
    var markerNear = marker is Pt mn && mn.x > 40 && mn.y > 40 && mn.x < rect.width - 40 && mn.y < rect.height - 40;
    if (intent.timed && !markerNear && reading.arrow is Pt arrow1 && stitcher.player is Pt player1)
    {
        var left = hud.timer ?? 999;
        var inside = timeIcons.Where(c => c.Item1.x > 20 && c.Item1.y > 20 && c.Item1.x < rect.width - 20 && c.Item1.y < rect.height - 20).ToList();
        double Near((Pt, string) c) => M.Hypot(c.Item1.x - arrow1.x, c.Item1.y - arrow1.y);
        var candidates = left <= 40 ? timeIcons.ToList() : inside;
        if (lastTimeTarget is Pt lt && M.Hypot(lt.x - player1.x, lt.y - player1.y) < 35) { doneTargets.Add(lt); lastTimeTarget = null; }
        candidates = candidates.Where(c => {
            var p = new Pt(player1.x + c.Item1.x - arrow1.x, player1.y + c.Item1.y - arrow1.y);
            if (doneTargets.Any(d => M.Hypot(d.x - p.x, d.y - p.y) < 120)) return false;
            if (Near(c) < 35) return false;
            if (c.Item2 != "beacon" && left > 40 && Near(c) > 120) return false;
            return true;
        }).ToList();
        (Pt, string)? chosen = null;
        if (lastTimeTarget is Pt last2)
        {
            var kept = candidates.Where(c => M.Hypot(player1.x + c.Item1.x - arrow1.x - last2.x, player1.y + c.Item1.y - arrow1.y - last2.y) < 50).ToList();
            chosen = MinBy(kept, Near);
        }
        chosen ??= MinBy(candidates, Near);
        if (chosen is var b && b.HasValue)
        {
            marker = b.Value.Item1;
            lastTimeTarget = new Pt(player1.x + b.Value.Item1.x - arrow1.x, player1.y + b.Value.Item1.y - arrow1.y);
            if (time - beaconSaid >= 5) { beaconSaid = time; Console.WriteLine($"{time,7:F2} s  TIME TARGET {b.Value.Item2} at ({F0(b.Value.Item1.x)},{F0(b.Value.Item1.y)}), {timeIcons.Count} icons, {left} s left"); }
        }
        else lastTimeTarget = null;
    }
    Pt? markerInBox = null;
    if (marker is Pt mk && reading.arrow is Pt arrow2)
    {
        markerBearing = Math.Atan2(mk.x - arrow2.x, -(mk.y - arrow2.y));
        markerSeen = time;
        if (mk.x > 40 && mk.y > 40 && mk.x < rect.width - 40 && mk.y < rect.height - 40) markerInBox = mk;
        if (time - markerSaid >= 5)
        {
            markerSaid = time;
            Console.WriteLine($"{time,7:F2} s  MARKER at ({F0(mk.x)},{F0(mk.y)}) in the box, {Compass.word(markerBearing.Value)} of the player");
        }
    }
    else if (markerBearing != null && time - markerSeen > 3) markerBearing = null;
    samples++;
    if (time < settleUntil)
    {
        Console.WriteLine($"{time,7:F2} s  settling after the floor change");
        continue;
    }
    if (!reading.isLegible)
    {
        illegible++;
        Console.WriteLine($"{time,7:F2} s  illegible: contrast {reading.contrast.ToString("F1", ci)} floor {F0(reading.floorFraction * 100)}% arrow {(reading.arrow == null ? "no" : "yes")} busy {reading.busyFraction.ToString("F2", ci)}");
        continue;
    }
    var placement = stitcher.add(reading, time);
    if (placement.placed) { placed++; refusedSince = null; }
    else if (refusedSince is double since)
    {
        if (time - since > 2.5)
        {
            Console.WriteLine($"{time,7:F2} s  fits nowhere for 2.5 s: new map");
            stitcher.reset(); navigator.forget();
            refusedSince = null;
            settleUntil = time + 2.5;   // usually a fade: the new map begins after it settles
            continue;
        }
    }
    else refusedSince = time;
    if (spotTimes.Count == 2 && !spotMarked && time >= spotTimes[0]) spotMarked = stitcher.markSpot();
    var backToSpot = spotTimes.Count == 2 && time >= spotTimes[1];
    var rule = AreaRule.none;
    if (intent.stayIn is string s && stitcher.areaIndex(s) is int si) rule = AreaRule.stay(si);
    if (intent.leave is string l && stitcher.areaIndex(l) is int li) rule = AreaRule.leave(li);
    if (intent.destination is string d && stitcher.areaMatching(d) is int di && di != stitcher.currentArea)
        rule = AreaRule.goTo(di, intent.leave is string l2 ? stitcher.areaIndex(l2) : null);
    Pt? markerPoint = null;
    if (markerInBox is Pt mib && reading.arrow is Pt a3 && stitcher.player is Pt p3) markerPoint = new Pt(p3.x + mib.x - a3.x, p3.y + mib.y - a3.y);
    var guidance = navigator.plan(stitcher, toMarks: leadToMarks || intent.slay, toSpot: backToSpot,
                                  toArches: leadToArches || (intent.travel && !intent.timed), areaRule: rule,
                                  marker: noMarker ? null : markerBearing, markerPoint: markerPoint, timed: intent.timed);
    lastGuidance = guidance;
    var headingValue = stitcher.heading();
    var heading = headingValue is double hv ? Compass.word(hv) : "still";
    if (reading.facing is double fc && headingValue is double mv)
        Console.WriteLine($"{time,7:F2} s  FACING {F0(fc * 180 / Math.PI)} {F0(mv * 180 / Math.PI)}");
    if (placement.placed)
        foreach (var (kind, point) in stitcher.newLandmarks)
        {
            var player = stitcher.player ?? point;
            Console.WriteLine($"{time,7:F2} s  LANDMARK {Name(kind)} {Compass.word(Math.Atan2(point.x - player.x, -(point.y - player.y)))} of the player, {F0(M.Hypot(point.x - player.x, point.y - player.y))} px");
        }
    string guideText = "no opening";
    if (guidance is Guidance g)
    {
        var tag = g.toMark ? " TO MARK" : g.toSpot ? " TO SPOT" : g.toArea ? " TO AREA" : g.toWell ? " TO WELL" : g.toArch ? " TO ARCH"
                : g.markerInView ? " TO MARKER IN VIEW" : g.beeline ? " BEELINE" : g.toMarker ? " TO MARKER" : "";
        // The nearest mark's distance from the player, so a stall beside enemies can be told from a wall.
        string nearestMark = reading.arrow is Pt ap && reading.marks.Length > 0
            ? $" (nearest {F0(reading.marks.Min(m => M.Hypot(m.x - ap.x, m.y - ap.y)))} px)" : "";
        guideText = $"go {Compass.word(g.bearing)} ({F0(g.bearing * 180 / Math.PI)}°), {F0(g.distance)} px, {g.openings} openings, {g.marks} marks{nearestMark}{tag}";
    }
    Console.WriteLine($"{time,7:F2} s  contrast {reading.contrast.ToString("F1", ci),4}  shift {Signed3(placement.dx)},{Signed3(placement.dy)} score {placement.score.ToString("F2", ci)} {(placement.placed ? "placed " : "REFUSED")}  moving {heading}  {guideText}");
}
ffmpeg.WaitForExit();
Console.WriteLine($"{samples} samples, {placed} placed, {illegible} illegible, {stitcher.recentres} re-centres. Output in {Path.GetFullPath(outPath).Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "~")}");
return 0;

// Swift's min(by:) returns the first minimal element.
static (Pt, string)? MinBy(List<(Pt, string)> items, Func<(Pt, string), double> key)
{
    (Pt, string)? best = null; double bestKey = double.PositiveInfinity;
    foreach (var item in items) { var k = key(item); if (best == null || k < bestKey) { best = item; bestKey = k; } }
    return best;
}
