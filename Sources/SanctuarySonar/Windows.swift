import AppKit
import ScreenCaptureKit

/// One window the owner could choose to record, as it is shown in the list.
struct GameWindow: Identifiable, Hashable {
    let id: CGWindowID
    let appName: String
    let bundleID: String
    let title: String
    let pixelWidth: Int
    let pixelHeight: Int
    let isStreamApp: Bool
    /// A game running through a Windows translation layer (CrossOver, Whisky, Wine).
    let isGame: Bool

    /// What the list says. The app first, because that is what the owner is choosing
    /// between; the title only when it adds something; the size because a streaming app can
    /// own a small launcher window and a large stream window with the same name.
    var label: String {
        var text = appName
        if !title.isEmpty && title != appName { text += ": \(title)" }
        return text + ", \(pixelWidth) by \(pixelHeight)"
    }
}

/// The apps a game is streamed through. Anything else can still be chosen; these are listed
/// first and are what a remembered choice falls back to.
enum StreamApps {
    static let names: [String: String] = [
        // Shadow is two apps: the stream is drawn by ShadowPCDisplay, a separate process
        // from the Electron launcher. Recording the launcher window gives no frames at all
        // while the game plays (measured 25 Sep 2026), so the stream is listed first.
        "com.blade.shadow-macos": "Shadow PC",
        "com.electron.shadow": "Shadow PC launcher",
        "com.nvidia.gfnpc.mall": "GeForce NOW",
        "com.playstation.RemotePlay": "PS Remote Play"
        // Muteny (the owner's private launcher) used to be listed here by bundle identifier;
        // since 29 Sep 2026 Diablo IV runs locally in a CrossOver bottle and is found by its
        // window (below), so the entry is gone — and with it the identifier, which carried a name.
    ]
}

/// Games run through a Windows translation layer show up as their executable — "Diablo IV.exe"
/// — with no bundle identifier at all. Measured 29 Sep 2026 with Diablo IV in a CrossOver
/// bottle: the game window is titled "Diablo IV", 1470×956 points, and the same process also
/// owns untitled 1470×33 and 500×500 helper windows; the title was missing from the game window
/// itself for a stretch (three refreshes over half a minute), so a title cannot be required.
enum GameApps {
    static func isWindowsExecutable(_ name: String) -> Bool { name.lowercased().hasSuffix(".exe") }
    static func displayName(_ name: String) -> String { isWindowsExecutable(name) ? String(name.dropLast(4)) : name }
    /// Processes of a bottle that are never the game.
    static let notGames: Set<String> = [
        "steam.exe", "steamwebhelper.exe", "steamservice.exe", "steamerrorreporter.exe",
        "explorer.exe", "services.exe", "winedevice.exe", "plugplay.exe", "rpcss.exe",
        "conhost.exe", "svchost.exe", "wineboot.exe", "start.exe", "battle.net.exe", "agent.exe"
    ]
    static func isGame(appName: String) -> Bool {
        isWindowsExecutable(appName) && !notGames.contains(appName.lowercased())
    }
}

/// Lists recordable windows and remembers which one the owner chose, by app and title rather
/// than by window number — the number changes every time the streaming app is reopened.
enum WindowList {
    private static let chosenBundleKey = "chosenBundleID"
    private static let chosenAppKey = "chosenAppName"
    private static let chosenTitleKey = "chosenTitle"

    /// Every window worth offering, stream apps first, with the SCWindow each one captures.
    ///
    /// Not on-screen-only: a full-screen stream lives on its own Space, and asking for
    /// on-screen windows from this app's window on the desktop would leave it out.
    static func load() async throws -> [(GameWindow, SCWindow)] {
        let content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: false)
        let ownPID = ProcessInfo.processInfo.processIdentifier
        var found: [(GameWindow, SCWindow)] = []
        for window in content.windows {
            guard let app = window.owningApplication,
                  app.processID != ownPID,
                  window.frame.width >= 320, window.frame.height >= 200 else { continue }
            let isGame = GameApps.isGame(appName: app.applicationName)
            // Wine lifts a full-screen game window above the menu bar (layer 26, measured
            // 29 Sep 2026) for exactly as long as the game is the app in front — which is
            // when the owner presses the chord. Ordinary windows stay at layer 0.
            guard window.windowLayer == 0 || (isGame && window.windowLayer <= 30) else { continue }
            let bundleID = app.bundleIdentifier
            let isStream = StreamApps.names[bundleID] != nil
            let title = window.title ?? ""
            // Hidden helper windows are everywhere once off-screen windows are included;
            // an untitled window from an ordinary app is almost always one of them. A game's
            // own window may be untitled for a while (Wine), so for a game the size decides.
            let gameSized = window.frame.width >= 800 && window.frame.height >= 600
            guard isStream || !title.isEmpty || (isGame && gameSized) else { continue }
            let scale = NSScreen.main?.backingScaleFactor ?? 2
            let name = StreamApps.names[bundleID] ?? GameApps.displayName(app.applicationName)
            let entry = GameWindow(id: window.windowID, appName: name, bundleID: bundleID,
                                   title: title,
                                   pixelWidth: Int(window.frame.width * scale),
                                   pixelHeight: Int(window.frame.height * scale),
                                   isStreamApp: isStream, isGame: isGame)
            found.append((entry, window))
        }
        found.sort { a, b in
            if a.0.isGame != b.0.isGame { return a.0.isGame }
            if a.0.isStreamApp != b.0.isStreamApp { return a.0.isStreamApp }
            let aLast = preferredLast.contains(a.0.bundleID), bLast = preferredLast.contains(b.0.bundleID)
            if aLast != bLast { return bLast }
            if a.0.appName != b.0.appName { return a.0.appName < b.0.appName }
            return a.0.pixelWidth * a.0.pixelHeight > b.0.pixelWidth * b.0.pixelHeight
        }
        return found
    }

    static func remember(_ window: GameWindow) {
        UserDefaults.standard.set(window.bundleID, forKey: chosenBundleKey)
        UserDefaults.standard.set(window.appName, forKey: chosenAppKey)
        UserDefaults.standard.set(window.title, forKey: chosenTitleKey)
    }

    /// Which window to select after a refresh: the same window if it is still there, else a
    /// game's own window (a running game is what the guide is for), else the remembered app
    /// and title, else the remembered app's largest window, else the largest window of any
    /// streaming app. The list is sorted largest-first within an app. Apps are matched by
    /// bundle identifier when they have one and by name when they do not (Wine games).
    static func pick(from windows: [GameWindow], current: CGWindowID?) -> GameWindow? {
        if let current, let same = windows.first(where: { $0.id == current }) { return same }
        if let game = windows.first(where: { $0.isGame }) { return game }
        let bundle = UserDefaults.standard.string(forKey: chosenBundleKey) ?? ""
        let app = UserDefaults.standard.string(forKey: chosenAppKey) ?? ""
        let title = UserDefaults.standard.string(forKey: chosenTitleKey)
        func sameApp(_ w: GameWindow) -> Bool {
            bundle.isEmpty ? (!app.isEmpty && w.appName == app) : w.bundleID == bundle
        }
        if let exact = windows.first(where: { sameApp($0) && $0.title == title }) { return exact }
        if let same = windows.first(where: sameApp) { return same }
        return windows.first(where: { $0.isStreamApp && !preferredLast.contains($0.bundleID) })
            ?? windows.first(where: { $0.isStreamApp })
    }

    /// Stream apps whose windows are never the game itself.
    private static let preferredLast: Set<String> = ["com.electron.shadow"]
}
