import AppKit
import SwiftUI
import Carbon.HIToolbox
import Keel
import Cartography

// The log goes to ~/Library/Application Support/Sanctuary Sonar/logs. Set before anything
// logs: Keel's log reads it once, on first use.
AppLog.appName = "Sanctuary Sonar"

/// Plain AppKit rather than a SwiftUI App: a SwiftUI Window scene opens and reopens itself
/// on its own terms (Muteny's trap list), and this app wants one window it controls plus a
/// menu bar item that exists for the app's whole life.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    let model = RecorderModel()
    let guide = Guide()
    private lazy var mapPoints = MapPointsController(model: model)
    private var window: NSWindow?
    private var statusItem: NSStatusItem?

    func applicationDidFinishLaunching(_ notification: Notification) {
        log("Sanctuary Sonar pid \(ProcessInfo.processInfo.processIdentifier), recordings in \(RecorderModel.folder.path)")
        buildMainMenu()
        buildStatusItem()
        model.onChange = { [weak self] in self?.updateStatusTitle() }
        guide.onChange = { [weak self] in self?.updateStatusTitle() }

        // `Sanctuary Sonar --map-points <png>…`: runs the map-screen icon finder on pictures of
        // the game window, logs what it finds, writes a ringed copy beside each, and quits.
        if let flag = CommandLine.arguments.firstIndex(of: "--map-points") {
            for path in CommandLine.arguments.dropFirst(flag + 1) {
                guard let image = NSImage(contentsOfFile: path)?.cgImage(forProposedRect: nil, context: nil, hints: nil),
                      let bgra = MapPointsController.bgra(of: image) else { print("no image at \(path)"); continue }
                let began = CACurrentMediaTime()
                let (points, player, diamond) = MapIcons.find(bgra: bgra, width: image.width, height: image.height)
                var counts: [String: Int] = [:]
                for q in points { counts[q.kind, default: 0] += 1 }
                print("\(path): \(points.count) points in \(Int((CACurrentMediaTime() - began) * 1000)) ms; player \(diamond ? "diamond" : "centre") at \(Int(player.x)),\(Int(player.y)); "
                      + counts.sorted { $0.value > $1.value }.map { "\($0.value) \($0.key)" }.joined(separator: ", "))
                for q in points.prefix(12) { print("  " + MapIcons.describe(q, width: image.width) + "  (\(q.x),\(q.y))") }
                if let ringed = MapIcons.ringed(image, points: points, player: player) {
                    let out = URL(fileURLWithPath: path).deletingPathExtension().appendingPathExtension("ringed.png")
                    _ = Recorder.writePNG(ringed, to: out)
                }
            }
            exit(0)
        }
        // Keys are chosen clear of VOCR's (Control-Shift-Command + S, V, C, E, A, R, G, U, Q):
        // found 30 Sep 2026 when every recording start also started VOCR's real-time OCR
        // ("OCR recording started" in the system voice, then the whole screen read aloud).
        HotKeys.register("guide", keyCode: kVK_ANSI_T) { [weak self] in self?.toggleGuide() }
        HotKeys.register("where", keyCode: kVK_ANSI_W) { [weak self] in self?.guide.sayWhere() }
        HotKeys.register("new map", keyCode: kVK_ANSI_N) { [weak self] in self?.guide.newMap() }
        HotKeys.register("objective", keyCode: kVK_ANSI_O) { [weak self] in self?.guide.sayObjective() }
        HotKeys.register("mark spot", keyCode: kVK_ANSI_X) { [weak self] in self?.guide.markSpot() }
        HotKeys.register("take me back", keyCode: kVK_ANSI_B) { [weak self] in self?.guide.takeMeBack() }
        HotKeys.register("describe", keyCode: kVK_ANSI_D) { [weak self] in self?.guide.describeMap() }
        HotKeys.register("louder", keyCode: kVK_ANSI_Equal) { [weak self] in self?.guide.louder() }
        HotKeys.register("quieter", keyCode: kVK_ANSI_Minus) { [weak self] in self?.guide.quieter() }
        // Vision's text model takes many seconds to load the first time; load it now, not
        // when the guide first reads the tracker.
        DispatchQueue.global(qos: .utility).async { TrackerReader.warmUp(); log("Text recognition ready") }

        HotKeys.register("map points", keyCode: kVK_ANSI_L) { [weak self] in
            guard let self else { return }
            Task { await self.mapPoints.next() }
        }
        HotKeys.register("previous point", keyCode: kVK_ANSI_K) { [weak self] in
            guard let self else { return }
            Task { await self.mapPoints.previous() }
        }
        HotKeys.register("go to point", keyCode: kVK_ANSI_J) { [weak self] in
            guard let self else { return }
            Task { await self.mapPoints.goToPoint() }
        }
        HotKeys.register("record", keyCode: kVK_ANSI_F) { [weak self] in self?.model.toggleRecording() }
        HotKeys.register("mark", keyCode: kVK_ANSI_M) { [weak self] in self?.model.mark() }
        HotKeys.register("picture", keyCode: kVK_ANSI_P) { [weak self] in
            guard let self else { return }
            Task { await self.model.savePicture() }
        }

        if let flag = CommandLine.arguments.firstIndex(of: "--guide-test") {
            runGuideTest(seconds: Double(CommandLine.arguments.dropFirst(flag + 1).first ?? "") ?? 20)
            return
        }

        Announcer.requestVoiceOverPermissionInBackground()
        showWindow()
        Task { await model.refresh() }
    }

    /// `Sanctuary Sonar --guide-test [seconds]`: runs the guide on the remembered window with
    /// no sound at all, logs what it reads, saves its map to the log folder, and quits.
    /// For checking the live path from outside without talking over the player.
    private func runGuideTest(seconds: Double) {
        log("Guide test for \(Int(seconds)) s")
        guide.silentTest = true
        Task {
            await guide.start(await model.captureTarget())
            Timer.common(seconds, repeats: false) { [weak self] _ in
                MainActor.assumeIsolated {
                    self?.guide.stop()
                    Timer.common(1.5, repeats: false) { _ in NSApp.terminate(nil) }
                }
            }
        }
    }

    func applicationDidBecomeActive(_ notification: Notification) {
        // Coming back to the app is when the owner wants the list to be current.
        Task { await model.refresh() }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showWindow()
        return true
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guide.stop()
        guard model.isRecording else { return .terminateNow }
        log("Quit while recording: finishing the file first")
        model.stopForQuit { NSApp.reply(toApplicationShouldTerminate: true) }
        return .terminateLater
    }

    // MARK: Window

    @objc func showWindow() {
        if window == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 520, height: 420),
                                  styleMask: [.titled, .closable, .miniaturizable],
                                  backing: .buffered, defer: false)
            window.title = "Sanctuary Sonar"
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: ContentView(model: model, guide: guide, mapPoints: mapPoints))
            window.center()
            self.window = window
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    // MARK: Menus

    private func buildMainMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Quit Sanctuary Sonar", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)
        let windowItem = NSMenuItem()
        let windowMenu = NSMenu(title: "Window")
        windowMenu.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        windowMenu.addItem(withTitle: "Minimise", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        windowItem.submenu = windowMenu
        main.addItem(windowItem)
        NSApp.mainMenu = main
    }

    private func buildStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.title = "DG"
        item.button?.setAccessibilityLabel("Sanctuary Sonar")
        let menu = NSMenu()
        menu.delegate = self
        item.menu = menu
        statusItem = item
    }

    /// Rebuilt each time it opens, so its first row always says what pressing it will do.
    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        menu.addItem(NSMenuItem(title: guide.isOn ? "Stop the guide" : "Start the guide",
                                action: #selector(toggleGuideFromMenu), keyEquivalent: ""))
        if guide.isOn {
            menu.addItem(NSMenuItem(title: "Say the objective", action: #selector(sayObjective), keyEquivalent: ""))
            menu.addItem(NSMenuItem(title: "Say where the guide leads", action: #selector(sayWhere), keyEquivalent: ""))
            menu.addItem(NSMenuItem(title: "Describe what's on the map", action: #selector(describeMap), keyEquivalent: ""))
            menu.addItem(NSMenuItem(title: "Mark this spot", action: #selector(markSpot), keyEquivalent: ""))
            menu.addItem(NSMenuItem(title: "Take me back to the marked spot", action: #selector(takeMeBack), keyEquivalent: ""))
            menu.addItem(NSMenuItem(title: "Start a new map", action: #selector(newMap), keyEquivalent: ""))
        }
        menu.addItem(.separator())
        let record = NSMenuItem(title: model.isRecording ? "Stop recording" : "Start recording",
                                action: #selector(toggleRecording), keyEquivalent: "")
        menu.addItem(record)
        if model.isRecording {
            menu.addItem(NSMenuItem(title: "Mark this moment", action: #selector(mark), keyEquivalent: ""))
        }
        menu.addItem(NSMenuItem(title: "Save a picture of the window", action: #selector(picture), keyEquivalent: ""))
        menu.addItem(.separator())
        let chosen = model.selected?.label ?? "none"
        let info = NSMenuItem(title: "Window: \(chosen)", action: nil, keyEquivalent: "")
        info.isEnabled = false
        menu.addItem(info)
        menu.addItem(NSMenuItem(title: "Show Sanctuary Sonar", action: #selector(showWindow), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "Open the recordings folder", action: #selector(openFolder), keyEquivalent: ""))
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "Quit Sanctuary Sonar", action: #selector(NSApplication.terminate(_:)), keyEquivalent: ""))
        for item in menu.items where item.action != #selector(NSApplication.terminate(_:)) { item.target = self }
    }

    /// "DG" at rest; a dot while recording, "G" while guiding.
    private func updateStatusTitle() {
        var title = "DG"
        if guide.isOn { title = "G " + title }
        if model.isRecording { title = "● " + title }
        statusItem?.button?.title = title
    }

    private func toggleGuide() {
        if guide.isOn { guide.stop() } else { Task { await guide.start(await model.captureTarget()) } }
    }

    @objc private func toggleGuideFromMenu() { toggleGuide() }
    @objc private func sayWhere() { guide.sayWhere() }
    @objc private func sayObjective() { guide.sayObjective() }
    @objc private func describeMap() { guide.describeMap() }
    @objc private func newMap() { guide.newMap() }
    @objc private func markSpot() { guide.markSpot() }
    @objc private func takeMeBack() { guide.takeMeBack() }
    @objc private func toggleRecording() { model.toggleRecording() }
    @objc private func mark() { model.mark() }
    @objc private func picture() { Task { await model.savePicture() } }
    @objc private func openFolder() { model.openFolder() }
}

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    app.setActivationPolicy(.regular)
    app.run()
}
