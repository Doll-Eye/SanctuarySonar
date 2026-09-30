import AppKit
import ScreenCaptureKit
import Keel

/// The recorder's state and every action on it, shared by the window, the menu bar item and
/// the global keys, so all three always agree.
@MainActor
final class RecorderModel: ObservableObject {

    @Published private(set) var windows: [GameWindow] = []
    @Published var selectedID: CGWindowID? {
        didSet {
            guard selectedID != oldValue, let chosen = selected else { return }
            // Only the owner's own choice is remembered; an automatic pick after a refresh
            // used to be stored too, which is how "Muteny" became the remembered game.
            if !autoPicking { WindowList.remember(chosen) }
            log("\(autoPicking ? "Picked" : "Chose") window: \(chosen.label) [\(chosen.bundleID)]")
        }
    }
    private var autoPicking = false
    @Published private(set) var isRecording = false
    @Published private(set) var status = "Not recording."
    @Published private(set) var permissionMissing = false

    /// Called whenever something the menu bar item shows has changed.
    var onChange: (() -> Void)?

    private var captureWindows: [CGWindowID: SCWindow] = [:]
    private let recorder = Recorder()
    private var recording: (url: URL, notes: URL, label: String, started: Date)?
    private var progressTimer: Timer?
    private var warnedNoPicture = false
    private var lastReminder = 0
    private var pendingQuit: (() -> Void)?

    var selected: GameWindow? { windows.first { $0.id == selectedID } }

    static var folder: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Movies/Dungeon Guide", isDirectory: true)
    }

    init() {
        recorder.onStopped = { [weak self] url, error in
            self?.recordingStopped(url: url, error: error)
        }
    }

    // MARK: Windows

    func refresh() async {
        do {
            let loaded = try await WindowList.load()
            permissionMissing = false
            windows = loaded.map(\.0)
            captureWindows = Dictionary(loaded.map { ($0.0.id, $0.1) }, uniquingKeysWith: { a, _ in a })
            log("Windows: " + (windows.isEmpty ? "none" : windows.map { "\($0.label) [\($0.bundleID)]" }.joined(separator: "; ")))
            if !isRecording {
                let pick = WindowList.pick(from: windows, current: selectedID)
                if pick?.id != selectedID { autoPicking = true; selectedID = pick?.id; autoPicking = false }
            }
        } catch {
            // The first listing is what makes macOS ask for Screen Recording; a refusal
            // lands here, and so does "granted, but not until the app is reopened".
            permissionMissing = !CGPreflightScreenCaptureAccess()
            log("Could not list windows: \(error.localizedDescription); screen recording allowed: \(!permissionMissing)")
            if permissionMissing { status = "Screen Recording is not allowed yet." }
        }
        onChange?()
    }

    /// The chosen window, freshly listed, for anything else that captures it (the guide).
    func captureTarget() async -> (GameWindow, SCWindow)? {
        await refresh()
        guard let chosen = selected, let window = captureWindows[chosen.id] else { return nil }
        return (chosen, window)
    }

    func openScreenRecordingSettings() {
        CGRequestScreenCaptureAccess()
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(url)
        }
    }

    // MARK: Recording

    func toggleRecording() {
        if isRecording { stop() } else { Task { await start() } }
    }

    func start() async {
        guard !isRecording else { return }
        await refresh()
        guard let chosen = selected, let window = captureWindows[chosen.id] else {
            tell("Choose a window first.", sound: "Basso")
            return
        }
        do {
            try FileManager.default.createDirectory(at: Self.folder, withIntermediateDirectories: true)
        } catch {
            tell("Could not make the recordings folder: \(error.localizedDescription)", sound: "Basso")
            return
        }
        if let free = try? Self.folder.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
            .volumeAvailableCapacityForImportantUsage, free < 5_000_000_000 {
            tell("Not recording: less than 5 gigabytes free.", sound: "Basso")
            return
        }
        let base = Self.fileStamp() + " " + chosen.appName
        let url = Self.folder.appendingPathComponent(base + ".mov")
        let notes = Self.folder.appendingPathComponent(base + ".txt")
        do {
            let (width, height) = try await recorder.start(window: window, to: url)
            let started = Date()
            recording = (url, notes, chosen.appName, started)
            isRecording = true
            warnedNoPicture = false
            lastReminder = 0
            writeNotes("""
                Window: \(chosen.label)
                App: \(chosen.bundleID)
                Recorded at: \(width) by \(height) pixels, up to \(Recorder.framesPerSecond) frames a second
                Started: \(started)
                Marks (time from the start of the video):

                """)
            status = "Recording \(chosen.appName) since \(Self.clock(started))."
            tell("Recording \(chosen.appName).", sound: "Tink")
            progressTimer = Timer.common(10, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.checkProgress() }
            }
            Timer.common(5, repeats: false) { [weak self] _ in
                MainActor.assumeIsolated { self?.checkFirstPicture() }
            }
        } catch {
            tell("Could not record \(chosen.appName): \(error.localizedDescription)", sound: "Basso")
        }
        onChange?()
    }

    func stop() {
        guard isRecording else { return }
        status = "Finishing the recording…"
        recorder.stop()
    }

    /// Stops a recording and finishes its file before the app quits.
    func stopForQuit(then done: @escaping () -> Void) {
        guard isRecording else { done(); return }
        pendingQuit = done
        stop()
    }

    private func recordingStopped(url: URL?, error: Error?) {
        progressTimer?.invalidate()
        progressTimer = nil
        let finished = recording
        recording = nil
        if url == nil, let notes = finished?.notes {
            // No video, so no marks worth keeping.
            try? FileManager.default.removeItem(at: notes)
        }
        isRecording = false
        if let url, let finished {
            let minutes = Int(Date().timeIntervalSince(finished.started)) / 60
            let seconds = Int(Date().timeIntervalSince(finished.started)) % 60
            let length = minutes > 0 ? "\(minutes) min \(seconds) s" : "\(seconds) s"
            status = "Saved \(url.lastPathComponent), \(length)."
            if let error {
                tell("Recording stopped, \(error.localizedDescription). Saved \(length).", sound: "Bottle")
            } else {
                tell("Saved, \(length).", sound: "Bottle")
            }
        } else {
            let reason = error?.localizedDescription ?? "unknown reason"
            status = "Nothing saved: \(reason)."
            tell("Nothing saved: \(reason).", sound: "Basso")
        }
        onChange?()
        if let quit = pendingQuit { pendingQuit = nil; quit() }
    }

    private func checkProgress() {
        let counts = recorder.currentCounts()
        log("Recording: \(counts.frames) frames, \(counts.audioBuffers) audio buffers, \(counts.dropped) dropped, \(Int(counts.elapsed)) s of video")
        // A recording nobody knew was running filled 14 GB on 26 Sep. Stop before the disk
        // is full, and say every quarter of an hour that it is still going.
        if let free = try? Self.folder.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
            .volumeAvailableCapacityForImportantUsage, free < 3_000_000_000 {
            tell("Recording stopping: the disk is nearly full.", sound: "Basso")
            stop()
            return
        }
        let minutes = Int(counts.elapsed) / 60
        if minutes > 0, minutes % 15 == 0, minutes != lastReminder {
            lastReminder = minutes
            tell("Still recording, \(minutes) minutes.", sound: "Pop")
        }
    }

    /// A window on another Space, minimised, or showing nothing that changes gives no
    /// frames. The owner cannot see that, so say it once.
    private func checkFirstPicture() {
        guard isRecording, !warnedNoPicture, recorder.currentCounts().frames == 0 else { return }
        warnedNoPicture = true
        tell("No picture from that window yet. It may be the wrong window, or minimised.", sound: "Basso")
    }

    // MARK: Marks and pictures

    /// Notes the current moment in the recording's text file, so a spot the owner found
    /// interesting ("lost here", "map overlay on now") can be found again in the video.
    func mark() {
        guard isRecording, recording != nil else {
            tell("Not recording.", sound: "Basso")
            return
        }
        let elapsed = recorder.currentCounts().elapsed
        let stamp = Self.duration(elapsed)
        appendNotes("\(stamp) mark\n")
        log("Mark at \(stamp)")
        tell("Marked \(stamp).", sound: "Pop")
    }

    func savePicture() async {
        await refresh()
        guard let chosen = selected, let window = captureWindows[chosen.id] else {
            tell("Choose a window first.", sound: "Basso")
            return
        }
        do {
            try FileManager.default.createDirectory(at: Self.folder, withIntermediateDirectories: true)
            let image = try await Recorder.picture(of: window)
            let url = Self.folder.appendingPathComponent(Self.fileStamp() + " " + chosen.appName + ".png")
            guard Recorder.writePNG(image, to: url) else { throw RecorderError.writerRefused }
            log("Picture \(image.width)×\(image.height) → \(url.path)")
            if isRecording { appendNotes("\(Self.duration(recorder.currentCounts().elapsed)) picture \(url.lastPathComponent)\n") }
            tell("Picture saved.", sound: "Glass")
        } catch {
            tell("No picture: \(error.localizedDescription)", sound: "Basso")
        }
    }

    func openFolder() {
        try? FileManager.default.createDirectory(at: Self.folder, withIntermediateDirectories: true)
        NSWorkspace.shared.open(Self.folder)
    }

    // MARK: Helpers

    /// Speech through VoiceOver (nothing when it is off — Keel's rule) plus a system sound,
    /// which reaches a sighted player too, and which does not queue behind VoiceOver.
    private func tell(_ text: String, sound: String) {
        log("Say: \(text)")
        // The sound is for a sighted player; with VoiceOver on, the speech is the message.
        if !Announcer.isVoiceOverRunning { NSSound(named: sound)?.play() }
        Announcer.say(text)
    }

    private func writeNotes(_ text: String) {
        guard let notes = recording?.notes else { return }
        try? text.write(to: notes, atomically: true, encoding: .utf8)
    }

    private func appendNotes(_ text: String) {
        guard let notes = recording?.notes, let data = text.data(using: .utf8),
              let handle = try? FileHandle(forWritingTo: notes) else { return }
        handle.seekToEndOfFile()
        handle.write(data)
        try? handle.close()
    }

    private static func fileStamp() -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd HH.mm.ss"
        return formatter.string(from: Date())
    }

    private static func clock(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm"
        return formatter.string(from: date)
    }

    static func duration(_ seconds: TimeInterval) -> String {
        let whole = Int(seconds)
        return String(format: "%d:%02d:%02d.%d", whole / 3600, whole / 60 % 60, whole % 60,
                      Int((seconds - Double(whole)) * 10))
    }
}
