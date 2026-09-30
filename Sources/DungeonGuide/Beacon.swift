import AVFoundation
import os
import Keel

/// The guide's one continuous sound, modelled on Diablo IV's own Audio Navigation
/// Assistance so the two speak one language. The owner, 26 Sep 2026: the first beacon was
/// "a little different to the normal nav assist, which is distracting".
///
/// The game's pings, as SightlessKombat describes them: two widely spaced drum hits when you
/// are facing the wrong way, closer hits with a click when roughly right, the fastest double
/// hit with a ping when exactly on heading. So:
///
/// - **A drum hit**, panned towards the route (screen-anchored — the camera never turns).
/// - **Rhythm and accent say how well the character faces the route**: `.away` two slow
///   hits; `.near` closer hits and a click; `.on` a fast double hit and a ping.
///
/// Distance is not in the sound any more — "where" says it. Numbers are first guesses for
/// the owner's ears. The engine and its node are made once and live as long as the app
/// (Muteny's AVAudioEngine rule).
final class Beacon {

    enum Accuracy: Int { case away = 0, near, on }

    struct Params {
        var active = false
        var pan: Float = 0          // −1 left … 1 right
        /// Drum pitch: north an octave-ish above south. A pan alone cannot tell north from
        /// south — both are dead centre — and the owner heard "the same thump" for each
        /// (27 Sep). Up on the screen is up in pitch.
        var pitch: Float = 1
        var accuracy = Accuracy.away
        var volume: Float = 0.6
    }

    private let engine = AVAudioEngine()
    private let source: AVAudioSourceNode
    private let params = OSAllocatedUnfairLock(initialState: Params())
    private var configurationObserver: NSObjectProtocol?

    /// The owner's volume, 0…1, kept between launches.
    var volume: Double {
        get { UserDefaults.standard.object(forKey: "beaconVolume") as? Double ?? 0.7 }
        set {
            let v = max(0, min(1, newValue))
            UserDefaults.standard.set(v, forKey: "beaconVolume")
            params.withLock { $0.volume = Float(v) }
        }
    }

    /// Each pattern: its cycle length and the hits in it (time, with a click, with a ping).
    private static let patterns: [(cycle: Float, hits: [(at: Float, click: Bool, ping: Bool)])] = [
        (1.3, [(0, false, false), (0.65, false, false)]),     // away
        (0.9, [(0, false, false), (0.22, true, false)]),      // near
        (0.65, [(0, false, false), (0.11, false, true)])      // on
    ]

    init() {
        let format = AVAudioFormat(standardFormatWithSampleRate: 48_000, channels: 2)!
        let sampleRate = Float(format.sampleRate)
        let params = self.params
        // Render-thread state, touched only inside the block.
        var t: Float = 0, pattern = 0, nextHit = 0
        var drumAge: Float = 1, drumPhase: Float = 0
        var clickAge: Float = 1, pingAge: Float = 1, pingPhase: Float = 0
        var noise: UInt32 = 0x12345678
        var current = Params()
        var gainL: Float = 0, gainR: Float = 0

        source = AVAudioSourceNode(format: format) { _, _, frameCount, bufferList -> OSStatus in
            if let latest = params.withLockIfAvailable({ $0 }) { current = latest }
            let buffers = UnsafeMutableAudioBufferListPointer(bufferList)
            let left = buffers[0].mData!.assumingMemoryBound(to: Float.self)
            let right = buffers[1].mData!.assumingMemoryBound(to: Float.self)
            let dt = 1 / sampleRate
            let angle = (current.pan + 1) * Float.pi / 4
            let targetL = cos(angle) * current.volume, targetR = sin(angle) * current.volume
            let wanted = current.accuracy.rawValue
            for frame in 0..<Int(frameCount) {
                gainL += (targetL - gainL) * 0.001
                gainR += (targetR - gainR) * 0.001
                // Advance the rhythm; a new accuracy takes effect at the next cycle, so a
                // change never cuts a pattern off mid-beat.
                t += dt
                let cycle = Beacon.patterns[pattern].cycle
                if t >= cycle {
                    t -= cycle
                    pattern = wanted
                    nextHit = 0
                }
                let active = current.active
                if active, nextHit < Beacon.patterns[pattern].hits.count, t >= Beacon.patterns[pattern].hits[nextHit].at {
                    let hit = Beacon.patterns[pattern].hits[nextHit]
                    drumAge = 0; drumPhase = 0
                    if hit.click { clickAge = 0 }
                    if hit.ping { pingAge = 0; pingPhase = 0 }
                    nextHit += 1
                }
                var sample: Float = 0
                // Drum: a tone falling from 330 to 160 Hz with a fast decay and a short noise
                // transient — a wood-block knock rather than a bass thump ("too much bass",
                // the owner, 26 Sep).
                if drumAge < 0.14 {
                    let f = (160 + 170 * exp(-drumAge / 0.02)) * current.pitch
                    drumPhase += 2 * .pi * f * dt
                    noise = noise &* 1664525 &+ 1013904223
                    let n = Float(Int32(bitPattern: noise)) / Float(Int32.max)
                    sample += sin(drumPhase) * exp(-drumAge / 0.045) * 0.9 + n * exp(-drumAge / 0.003) * 0.4
                    drumAge += dt
                }
                // Click: 4 ms of noise.
                if clickAge < 0.006 {
                    noise = noise &* 1664525 &+ 1013904223
                    let n = Float(Int32(bitPattern: noise)) / Float(Int32.max)
                    sample += n * 0.6 * (1 - clickAge / 0.006)
                    clickAge += dt
                }
                // Ping: a bright bell.
                if pingAge < 0.35 {
                    pingPhase += 2 * .pi * 1320 * dt
                    sample += (sin(pingPhase) + 0.25 * sin(2.76 * pingPhase)) * exp(-pingAge / 0.09) * 0.5
                    pingAge += dt
                }
                left[frame] = sample * gainL
                right[frame] = sample * gainR
            }
            return noErr
        }
        engine.attach(source)
        engine.connect(source, to: engine.mainMixerNode, format: format)
        params.withLock { $0.volume = Float(volume) }
        // A change of output device stops the engine. Start it again — never rebuild it.
        configurationObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main) { [weak self] _ in
            log("Beacon: audio configuration changed")
            DispatchQueue.main.async { self?.startEngine() }
        }
    }

    func startEngine() {
        guard !engine.isRunning else { return }
        do { try engine.start() } catch { log("Beacon engine did not start: \(error.localizedDescription)") }
    }

    /// Points the beacon. `bearing` is radians clockwise from screen-up.
    func point(bearing: Double, accuracy: Accuracy) {
        params.withLock {
            $0.active = true
            $0.pan = Float(sin(bearing)) * 0.9
            // North ×1.41, east and west ×1, south ×0.71: a whole octave between up and down.
            $0.pitch = Float(pow(2.0, cos(bearing) * 0.5))
            $0.accuracy = accuracy
        }
    }

    func silence() {
        params.withLock { $0.active = false }
    }
}
