// The window: the same functions as the hot keys, as buttons, plus the game window to read,
// the beacon volume and the recent log. Plain Windows Forms controls, so NVDA and JAWS read
// every control by its label with no extra work; every button carries its hot key in its
// text so the window doubles as the cheat sheet. Nothing here touches the guide directly:
// every click is posted to the worker thread, and a timer copies the guide's state back.
namespace SanctuarySonar.Shell;

public sealed class MainWindow : Form
{
    readonly Shell shell;
    readonly TextBox statusBox = new();
    readonly ComboBox windowBox = new();
    readonly Button guideButton = new();
    readonly CheckBox beaconsBox = new();
    readonly Button recordButton = new();
    readonly ListBox pointsList = new();
    bool settingPoints = false;

    void refreshPoints()
    {
        settingPoints = true;
        try
        {
            pointsList.BeginUpdate();
            pointsList.Items.Clear();
            var points = shell.mapPoints;
            for (int i = 0; i < points.Count; i++) pointsList.Items.Add($"{i + 1}. {MapIcons.describe(points[i], shell.mapWidth)}");
            if (shell.mapIndex >= 0 && shell.mapIndex < pointsList.Items.Count) pointsList.SelectedIndex = shell.mapIndex;
            pointsList.EndUpdate();
        }
        finally { settingPoints = false; }
    }
    readonly TrackBar volumeBar = new();
    readonly Label volumeValue = new();
    readonly TextBox logBox = new();
    readonly System.Windows.Forms.Timer refresh = new();
    bool settingVolume = false;
    int logLines = 0;
    const int logLinesKept = 400;

    /// <summary>Log lines the window leaves out: the once-a-second chatter that hides the
    /// decisions (the same filter the notes recommend for reading a log file).</summary>
    static readonly string[] noise = { "] Timer: ", "] Map-screen check", "] HUD text", "] Guide: ", "] Frame " };

    public MainWindow(Shell shell)
    {
        this.shell = shell;
        Text = "Sanctuary Sonar";
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.Manual;
        // The window fits its content at whatever DPI the screen has (150 % on the Shadow PC cut
        // the log box and the bottom row off a fixed-size window).
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        Padding = new Padding(10);

        var table = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(10, 10) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(table);

        // Status.
        table.Controls.Add(label("Status"));
        statusBox.ReadOnly = true; statusBox.TabStop = true; statusBox.Dock = DockStyle.Fill;
        statusBox.AccessibleName = "Status";
        statusBox.Text = shell.status;
        table.Controls.Add(statusBox);

        // The game window.
        var windowRow = row();
        windowRow.Controls.Add(label("Game &window (part of its title)"));
        windowBox.DropDownStyle = ComboBoxStyle.DropDown;
        windowBox.Width = 300;
        windowBox.AccessibleName = "Game window, part of its title";
        windowBox.AccessibleDescription = "The window the guide reads. Pick one from the list or type part of its title.";
        windowBox.Text = shell.windowPart;
        windowBox.SelectionChangeCommitted += (_, _) => applyWindowChoice();
        windowBox.Leave += (_, _) => applyWindowChoice();
        windowRow.Controls.Add(windowBox);
        windowRow.Controls.Add(button("Refresh list", () => fillWindowList(), post: false));
        table.Controls.Add(windowRow);

        // The functions, one button each, hot key in the text.
        var buttons = row();
        buttons.WrapContents = true; buttons.MaximumSize = new Size(LogicalToDeviceUnits(700), 0);
        guideButton.AutoSize = true; guideButton.Text = guideText();
        guideButton.AccessibleDescription = "Starts or stops the guide. Control Shift Alt G from any app.";
        guideButton.Click += (_, _) => { applyWindowChoice(); shell.post(shell.toggleGuide); };
        buttons.Controls.Add(guideButton);
        buttons.Controls.Add(button("&Where does it lead  (Ctrl+Shift+Alt+W)", () => shell.guide.sayWhere()));
        buttons.Controls.Add(button("&Describe the map  (Ctrl+Shift+Alt+D)", () => shell.guide.describeMap(new List<string>())));
        buttons.Controls.Add(button("Say the &objective  (Ctrl+Shift+Alt+O)", () => shell.guide.sayObjective()));
        buttons.Controls.Add(button("Mark this &spot  (Ctrl+Shift+Alt+S)", () => shell.guide.markSpot()));
        buttons.Controls.Add(button("Take me &back to the spot  (Ctrl+Shift+Alt+B)", () => shell.guide.takeMeBack()));
        buttons.Controls.Add(button("Start a &new map  (Ctrl+Shift+Alt+N)", () => shell.guide.newMap()));
        table.Controls.Add(buttons);

        // Beacons: off unless wanted (they need lighting by hand).
        beaconsBox.Text = "Lead to beacons for time (they need lighting by hand; off means afflicted packs only)";
        beaconsBox.AutoSize = true; beaconsBox.Margin = new Padding(0, 6, 0, 2);
        beaconsBox.Checked = shell.leadToBeacons;
        beaconsBox.CheckedChanged += (_, _) => { if (shell.leadToBeacons != beaconsBox.Checked) shell.leadToBeacons = beaconsBox.Checked; };
        table.Controls.Add(beaconsBox);

        // Recording: the game window to an MP4 in the recordings folder, with the log beside it.
        var recordRow = row();
        recordRow.WrapContents = true; recordRow.MaximumSize = new Size(LogicalToDeviceUnits(700), 0);
        recordButton.AutoSize = true; recordButton.Text = recordText(); recordButton.Margin = new Padding(0, 4, 8, 4);
        recordButton.AccessibleDescription = "Records the game window to a video file, with the run's log beside it, for looking at what worked afterwards.";
        recordButton.Click += (_, _) => { applyWindowChoice(); shell.post(shell.toggleRecording); };
        recordRow.Controls.Add(recordButton);
        recordRow.Controls.Add(button("&Mark the recording  (Ctrl+Shift+Alt+M)", () => shell.markRecording()));
        recordRow.Controls.Add(button("Open the recordings folder", () => openFolder(shell.recordingsDir), post: false));
        table.Controls.Add(recordRow);

        // Map points: the world map's icons as a list; point at one, then click it.
        var mapRow = row();
        mapRow.WrapContents = true; mapRow.MaximumSize = new Size(LogicalToDeviceUnits(700), 0);
        mapRow.Controls.Add(button("Scan the map / next point  (Ctrl+Shift+Alt+L)", () => shell.nextPoint()));
        mapRow.Controls.Add(button("Previous point  (Ctrl+Shift+Alt+K)", () => shell.previousPoint()));
        mapRow.Controls.Add(button("Point at it, then click  (Ctrl+Shift+Alt+J)", () => shell.goToPoint()));
        table.Controls.Add(mapRow);
        table.Controls.Add(label("Map points (open the game's map first; nearest first)"));
        pointsList.Width = LogicalToDeviceUnits(700); pointsList.Height = LogicalToDeviceUnits(90);
        pointsList.AccessibleName = "Map points";
        pointsList.SelectedIndexChanged += (_, _) => { if (!settingPoints && pointsList.SelectedIndex >= 0) shell.post(() => shell.selectPoint(pointsList.SelectedIndex)); };
        pointsList.DoubleClick += (_, _) => shell.post(() => shell.goToPoint());
        table.Controls.Add(pointsList);
        shell.onMapPoints = () => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke(refreshPoints); } catch { } };

        // Beacon volume.
        var volumeRow = row();
        volumeRow.Controls.Add(label("Beacon &volume  (Ctrl+Shift+Alt+= louder, Ctrl+Shift+Alt+- quieter)"));
        volumeBar.Minimum = 0; volumeBar.Maximum = 100; volumeBar.TickFrequency = 10; volumeBar.SmallChange = 10; volumeBar.LargeChange = 10;
        volumeBar.Width = 300;
        volumeBar.AccessibleName = "Beacon volume, percent";
        volumeBar.Value = percent(shell.guide.beaconVolume);
        volumeBar.ValueChanged += (_, _) =>
        {
            volumeValue.Text = $"{volumeBar.Value} percent";
            if (settingVolume) return;
            var v = volumeBar.Value / 100.0;
            shell.post(() => shell.guide.beaconVolume = v);
        };
        volumeRow.Controls.Add(volumeBar);
        volumeValue.AutoSize = true; volumeValue.Text = $"{volumeBar.Value} percent"; volumeValue.Margin = new Padding(6, 8, 0, 0);
        volumeRow.Controls.Add(volumeValue);
        table.Controls.Add(volumeRow);

        // The recent log.
        table.Controls.Add(label("Recent &log (what it saw and said; the full log is in the log folder)"));
        logBox.Multiline = true; logBox.ReadOnly = true; logBox.ScrollBars = ScrollBars.Vertical; logBox.WordWrap = false;
        logBox.Width = LogicalToDeviceUnits(700); logBox.Height = LogicalToDeviceUnits(170); logBox.Font = new Font(FontFamily.GenericMonospace, 9);
        logBox.AccessibleName = "Recent log";
        table.Controls.Add(logBox);

        // Bottom row.
        var bottom = row();
        bottom.Controls.Add(button("Open the log &folder", () => openLogFolder(), post: false));
        bottom.Controls.Add(button("&Quit  (Ctrl+Shift+Alt+Q)", () => shell.quit(), post: false));
        table.Controls.Add(bottom);

        // Every row fits its content.
        for (int i = 0; i < table.Controls.Count; i++) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowCount = table.Controls.Count;

        // Log lines arrive on the worker thread; marshal them here. Start with what was logged
        // before the window existed (speech found, hot keys registered).
        foreach (var line in shell.linesBeforeWindow) appendLog(line);
        shell.onLog = line => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke(() => appendLog(line)); } catch { } };

        // The guide's state, copied back a few times a second (hot keys change it too).
        refresh.Interval = 300;
        refresh.Tick += (_, _) => sync();
        refresh.Start();

        // Bottom-left of the screen: away from the minimap (top-right) and the tracker column
        // (right), so if the window ever comes to the front over the game it covers neither.
        Load += (_, _) =>
        {
            var area = Screen.FromControl(this).WorkingArea;
            Location = new Point(area.Left + 20, Math.Max(area.Top, area.Bottom - Height - 20));
            fillWindowList();
            Log.log("Window open.");
        };
        FormClosing += (_, _) => shell.quit();
    }

    static Label label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 8, 8, 2) };

    static FlowLayoutPanel row() => new() { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };

    Button button(string text, Action action, bool post = true)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 4, 8, 4) };
        b.Click += (_, _) => { if (post) shell.post(action); else action(); };
        return b;
    }

    static int percent(double volume) => (int)Math.Round(Math.Clamp(volume, 0, 1) * 100, MidpointRounding.AwayFromZero);

    string guideText() => shell.guide.isOn ? "&Guide off  (Ctrl+Shift+Alt+G)" : "&Guide on  (Ctrl+Shift+Alt+G)";

    string recordText() => shell.recorder.isRecording ? "Stop &recording  (Ctrl+Shift+Alt+R)" : "&Record the run  (Ctrl+Shift+Alt+R)";

    void applyWindowChoice()
    {
        var text = windowBox.Text.Trim();
        if (text.Length > 0 && text != shell.windowPart) { shell.windowPart = text; Log.log($"Window to read: \"{text}\""); }
    }

    void fillWindowList()
    {
        var current = windowBox.Text;
        windowBox.Items.Clear();
        foreach (var w in GameWindow.list())
            if (w.title != Text) windowBox.Items.Add(w.title);
        windowBox.Text = current;
    }

    void sync()
    {
        var status = shell.status;
        if (statusBox.Text != status) statusBox.Text = status;
        var g = guideText();
        if (guideButton.Text != g) guideButton.Text = g;
        var r = recordText();
        if (recordButton.Text != r) recordButton.Text = r;
        if (beaconsBox.Checked != shell.leadToBeacons) beaconsBox.Checked = shell.leadToBeacons;
        var p = percent(shell.guide.beaconVolume);
        if (volumeBar.Value != p && !volumeBar.Focused)
        {
            settingVolume = true;
            try { volumeBar.Value = p; } finally { settingVolume = false; }
        }
    }

    void appendLog(string line)
    {
        foreach (var n in noise) if (line.Contains(n)) return;
        if (logLines >= logLinesKept)
        {
            // Drop the oldest half rather than growing without end.
            var lines = logBox.Lines;
            logBox.Lines = lines.Skip(lines.Length / 2).ToArray();
            logLines = logBox.Lines.Length;
        }
        logBox.AppendText(line + Environment.NewLine);
        logLines++;
    }

    void openLogFolder() => openFolder(Path.Combine(shell.dataDir, "logs"));

    static void openFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception e) { Log.log($"Could not open {path}: {e.Message}"); }
    }
}
