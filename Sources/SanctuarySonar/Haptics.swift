import Foundation
import Keel

/// The guide in the hands: the DualSense's voice-coil actuators, driven through the
/// controller's own audio device over USB (Keel's `Chime.hapticPulse`), left and right
/// separately.
///
/// The controller is opened non-exclusively, the way Muteny's reader does it, so the
/// streaming app keeps receiving every press; nothing here reads input at all.
///
/// Vocabulary, mirroring the beacon so the hand and the ear agree:
/// - **Direction**, once a second: a tap weighted to the side the route lies (screen left or
///   right), higher when it is up the screen, lower when it is down.
/// - **On course**: a short rising glide in both grips — "yes, this way".
/// - **Dead end**: a rough falling glide — "no".
///
/// Over Bluetooth the controller has no audio device and these do nothing (Keel's rumble
/// fallback is not used: it would send rumble reports into a controller the streaming app
/// is also driving). Unverified: whether the streaming app's own rumble reports switch the
/// actuators out of audio mode mid-game — the owner's hands will say.
@MainActor
final class Haptics {
    private let reader = DualSenseReader()
    private let feedback: Feedback
    private var started = false
    private var lastTap = Date.distantPast
    private var lastGlide = Date.distantPast

    /// The owner's switch. Stored so it survives a relaunch. Off unless switched on: the
    /// owner tried it on 25 Sep 2026 and found it annoying — "the sound is enough".
    var enabled: Bool {
        get { UserDefaults.standard.object(forKey: "vibration") as? Bool ?? false }
        set { UserDefaults.standard.set(newValue, forKey: "vibration") }
    }

    init() {
        feedback = Feedback(reader: reader)
    }

    /// Opens the controller and its audio route. Idempotent.
    func start() {
        guard !started else { return }
        started = true
        reader.onConnect = { [weak self] in
            guard let self else { return }
            self.feedback.refreshAudioRoute()
            // Tell the controller to keep the voice coils on the audio path rather than
            // emulating the old rumble motors (Muteny's setting, 0x30).
            self.feedback.applyAudioPath()
            log("Controller connected; vibration through audio: \(self.feedback.hapticsFromAudio)")
        }
        reader.onDisconnect = { [weak self] in
            self?.feedback.refreshAudioRoute()
            log("Controller disconnected")
        }
        reader.start()
        feedback.start(useControllerAudio: true)
    }

    var available: Bool { feedback.hapticsFromAudio }

    /// Called on every guidance update; taps at most once a second.
    func direction(bearing: Double) {
        guard enabled, available, Date().timeIntervalSince(lastTap) >= 1 else { return }
        lastTap = Date()
        let ahead = cos(bearing)
        let frequency = ahead > 0.38 ? 170.0 : (ahead < -0.38 ? 70.0 : 115.0)
        feedback.chime.hapticPulse(Chime.Haptic(from: frequency, strength: 0.5, duration: 0.07, decay: 35),
                                   balance: sin(bearing))
    }

    func onCourse() {
        guard enabled, available, Date().timeIntervalSince(lastGlide) >= 1.5 else { return }
        lastGlide = Date()
        lastTap = Date()   // do not tap on top of the glide
        feedback.chime.hapticPulse(Chime.Haptic(from: 90, to: 190, strength: 0.55, duration: 0.18, decay: 6))
    }

    func deadEnd() {
        guard enabled, available else { return }
        lastGlide = Date()
        lastTap = Date()
        feedback.chime.hapticPulse(Chime.Haptic(from: 170, to: 55, strength: 0.6, duration: 0.35,
                                                decay: 3, roughness: 0.6))
    }

    /// A test the owner can feel: left, then right, then the on-course glide.
    func test() {
        guard available else {
            log("Vibration test: no controller audio route")
            return
        }
        let tap = Chime.Haptic(from: 115, strength: 0.6, duration: 0.12, decay: 20)
        feedback.chime.hapticPulse(tap, balance: -1)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { self.feedback.chime.hapticPulse(tap, balance: 1) }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.3) {
            self.feedback.chime.hapticPulse(Chime.Haptic(from: 90, to: 190, strength: 0.55, duration: 0.18, decay: 6))
        }
    }
}
