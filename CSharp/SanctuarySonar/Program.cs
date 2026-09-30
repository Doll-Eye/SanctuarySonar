// Sanctuary Sonar for Windows — the shell around the shared core.
//
//     SanctuarySonar [--window "Diablo IV"] [--list] [--no-speech-at-start] [--busy N] [--pictures N]
//
// Opens a small window (MainWindow.cs) with a button for every function, starts the global
// hot keys (HotKeys.cs) and the worker loop (Shell.cs). Double-clicking the exe is the normal
// way to run it; the options are for testing.
using System.Runtime.InteropServices;
using SanctuarySonar.Cartography;

namespace SanctuarySonar.Shell;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string? windowPart = null;
        bool speakAtStart = true, list = false;
        int pictures = 3;
        double? busy = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--window" && i + 1 < args.Length) windowPart = args[++i];
            else if (args[i] == "--list") list = true;
            else if (args[i] == "--no-speech-at-start") speakAtStart = false;
            else if (args[i] == "--pictures" && i + 1 < args.Length && int.TryParse(args[++i], out var n)) pictures = n;
            else if (args[i] == "--busy" && i + 1 < args.Length && double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bl)) busy = bl;
        }

        // --frame <recording.mp4> <seconds> <out.bmp>: one frame of a recording as a picture, for
        // checking what was recorded (there is no ffmpeg on the Shadow PC).
        if (args.Length >= 4 && args[0] == "--frame")
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            try
            {
                var result = RecordingFrame.save(args[1], double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), args[3]);
                Console.WriteLine(result);
                return 0;
            }
            catch (Exception e) { Console.WriteLine($"Failed: {e.GetBaseException().Message}"); return 1; }
        }

        // --picture <out.bmp> [--window "…"]: one capture of the game window through PrintWindow
        // (the window's own picture, whatever is on top), to check that path works for a game.
        if (args.Length >= 2 && args[0] == "--picture")
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            var w = GameWindow.find(windowPart ?? "Diablo IV");
            if (w == null) { Console.WriteLine("No such window."); return 1; }
            using var g = new ScreenGrabber();
            bool ok = g.beginFrame(w);
            var px = g.grab(w.clientLeft, w.clientTop, w.clientWidth & ~1, w.clientHeight & ~1);
            if (px == null) { Console.WriteLine("Capture failed."); return 1; }
            Bmp.write(args[1], px, w.clientWidth & ~1, w.clientHeight & ~1);
            long sum = 0; for (int i = 0; i < px.Length; i += 4001) sum += px[i];
            Console.WriteLine($"{w}: PrintWindow {(ok ? "worked" : "failed, read the screen")}; mean sampled byte {sum / (px.Length / 4001)}; wrote {args[1]}");
            return 0;
        }

        // --map-points <frame.bmp> [out.bmp]: the map icons found in a saved frame, for checking the
        // finder against recordings; with an output path, the frame with each point ringed.
        if (args.Length >= 2 && args[0] == "--map-points")
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            using var bmp = new System.Drawing.Bitmap(args[1]);
            int W = bmp.Width & ~1, H = bmp.Height & ~1;
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, W, H), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var px = new byte[W * H * 4];
            for (int y = 0; y < H; y++) System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, px, y * W * 4, W * 4);
            bmp.UnlockBits(data);
            var began = System.Diagnostics.Stopwatch.StartNew();
            var (points, player, diamond) = MapIcons.find(px, W, H);
            Console.WriteLine($"{points.Count} points in {began.ElapsedMilliseconds} ms; player {(diamond ? "diamond" : "centre")} at {player.x:0},{player.y:0}");
            foreach (var p in points) Console.WriteLine($"  {MapIcons.describe(p, W)}  at {p.x},{p.y}  {p.distance:0} px");
            if (args.Length >= 3)
            {
                foreach (var p in points)
                    for (int a = 0; a < 360; a += 2)
                    {
                        int x = p.x + (int)(34 * Math.Cos(a * Math.PI / 180)), y = p.y + (int)(34 * Math.Sin(a * Math.PI / 180));
                        if (x < 0 || y < 0 || x >= W || y >= H) continue;
                        int i = (y * W + x) * 4; px[i] = 0; px[i + 1] = 255; px[i + 2] = 255;
                    }
                Bmp.write(args[2], px, W, H);
            }
            return 0;
        }

        if (list)
        {
            // A windowed exe has no console of its own; borrow the parent's if started from one.
            var windows = GameWindow.list().Select(w => w.ToString()).ToList();
            if (AttachConsole(ATTACH_PARENT_PROCESS)) { Console.WriteLine(); foreach (var w in windows) Console.WriteLine(w); }
            else MessageBox.Show(string.Join(Environment.NewLine, windows), "Windows Sanctuary Sonar can see");
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var shell = new Shell(pictures);
        if (windowPart != null) shell.windowPart = windowPart;

        // The minimap's fog texture reads busier on a natively rendered screen than over Shadow's
        // video (0.38–0.40 on the Shadow PC, 27 Sep, against 0.34 tuned on recordings), so the gate
        // that rejects the inventory screen sits higher here. --busy N overrides.
        MinimapReading.busyLimit = busy ?? 0.60;   // 0.45–0.49 seen on real minimap frames with the arrow found (14:09 run)
        Log.log($"Busy limit {MinimapReading.busyLimit}");

        shell.makeHotKeys().start();
        shell.start();
        if (speakAtStart) shell.speech.say("Sanctuary Sonar ready. Control Shift Alt G starts the guide.");

        Application.Run(new MainWindow(shell));
        return 0;
    }

    const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
    [DllImport("kernel32.dll")] static extern bool AttachConsole(uint processId);
}
