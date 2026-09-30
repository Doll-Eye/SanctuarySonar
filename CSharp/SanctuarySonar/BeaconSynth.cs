// Port of Sources/DungeonGuide/Beacon.swift — the sound only. The Swift wraps this in an
// AVAudioEngine / AVAudioSourceNode and keeps the volume in UserDefaults; here the shell's
// BeaconOut.cs wraps it in NAudio, persists the volume through Settings, and implements IBeacon.
using System;
using System.Threading;

namespace SanctuarySonar.Shell;

/// <summary>
/// The guide's one continuous sound, modelled on Diablo IV's own Audio Navigation Assistance so
/// the two speak one language. The game's pings, as SightlessKombat describes them: two widely
/// spaced drum hits when facing the wrong way, closer hits with a click when roughly right, the
/// fastest double hit with a ping when exactly on heading. So:
/// <list type="bullet">
/// <item>A drum hit, panned towards the route (screen-anchored — the camera never turns).</item>
/// <item>Rhythm and accent say how well the character faces the route: <c>away</c> two slow
/// hits; <c>near</c> closer hits and a click; <c>on</c> a fast double hit and a ping.</item>
/// </list>
/// Distance is not in the sound — "where" says it. Numbers are first guesses for the owner's ears.
/// Pure DSP: no audio API. Call <see cref="render"/> from the audio thread; everything else from
/// the control thread.
/// </summary>
public sealed class BeaconSynth
{
    public struct Params
    {
        public bool active;
        /// <summary>−1 left … 1 right.</summary>
        public float pan;
        /// <summary>Drum pitch: north an octave-ish above south. A pan alone cannot tell north
        /// from south — both are dead centre — and the owner heard "the same thump" for each
        /// (27 Sep). Up on the screen is up in pitch.</summary>
        public float pitch;
        public BeaconAccuracy accuracy;
        public float volume;

        // PORT NOTE: Swift's `Params()` carries these defaults. In C# they live in the
        // parameterless constructor, so use `new Params()`, never `default(Params)` (which
        // would give pitch 0 and volume 0).
        public Params()
        {
            active = false;
            pan = 0;
            pitch = 1;
            accuracy = BeaconAccuracy.away;
            volume = 0.6f;
        }
    }

    // Control-thread → audio-thread hand-off: written whole under the lock, read with a
    // try-lock (the Swift's `withLockIfAvailable`) so the render never blocks.
    private readonly object paramsLock = new();
    private Params shared = new();

    private double volumeValue = 0.7;

    /// <summary>The owner's volume, 0…1.</summary>
    // PORT NOTE: the Swift reads and writes UserDefaults "beaconVolume" (default 0.7) here.
    // Persistence belongs to the shell — BeaconOut should set this from
    // Settings.getDouble("beaconVolume", 0.7) after construction. The getter returns the last
    // value set, starting at 0.7 as the Swift's init does (`params.volume = Float(volume)`).
    public double volume
    {
        get => volumeValue;
        set
        {
            var v = Math.Max(0, Math.Min(1, value));
            volumeValue = v;
            lock (paramsLock) { shared.volume = (float)v; }
        }
    }

    /// <summary>Each pattern: its cycle length and the hits in it (time, with a click, with a ping).</summary>
    public static readonly (float cycle, (float at, bool click, bool ping)[] hits)[] patterns =
    {
        (1.3f, new[] { (0f, false, false), (0.65f, false, false) }),   // away
        (0.9f, new[] { (0f, false, false), (0.22f, true, false) }),    // near
        (0.65f, new[] { (0f, false, false), (0.11f, false, true) })    // on
    };

    // Render-thread state, touched only inside render().
    private float t = 0;
    private int pattern = 0, nextHit = 0;
    private float drumAge = 1, drumPhase = 0;
    private float clickAge = 1, pingAge = 1, pingPhase = 0;
    private uint noise = 0x12345678;
    private Params current = new();
    private float gainL = 0, gainR = 0;

    public BeaconSynth()
    {
        // Mirrors the Swift init's `params.withLock { $0.volume = Float(volume) }`.
        lock (paramsLock) { shared.volume = (float)volumeValue; }
    }

    /// <summary>
    /// The exact port of the AVAudioSourceNode render block. Fills <paramref name="frames"/>
    /// samples of <paramref name="left"/> and <paramref name="right"/> (non-interleaved).
    /// </summary>
    // PORT NOTE: the Swift fixes the sample rate at 48 000 when the format is made; here the
    // shell passes whatever rate NAudio opened, and dt follows it.
    public void render(float[] left, float[] right, int frames, float sampleRate)
    {
        if (Monitor.TryEnter(paramsLock))
        {
            try { current = shared; }
            finally { Monitor.Exit(paramsLock); }
        }
        var dt = 1f / sampleRate;
        var angle = (current.pan + 1f) * MathF.PI / 4f;
        var targetL = MathF.Cos(angle) * current.volume;
        var targetR = MathF.Sin(angle) * current.volume;
        var wanted = (int)current.accuracy;
        for (var frame = 0; frame < frames; frame++)
        {
            gainL += (targetL - gainL) * 0.001f;
            gainR += (targetR - gainR) * 0.001f;
            // Advance the rhythm; a new accuracy takes effect at the next cycle, so a change
            // never cuts a pattern off mid-beat.
            t += dt;
            var cycle = patterns[pattern].cycle;
            if (t >= cycle)
            {
                t -= cycle;
                pattern = wanted;
                nextHit = 0;
            }
            var active = current.active;
            if (active && nextHit < patterns[pattern].hits.Length && t >= patterns[pattern].hits[nextHit].at)
            {
                var hit = patterns[pattern].hits[nextHit];
                drumAge = 0; drumPhase = 0;
                if (hit.click) clickAge = 0;
                if (hit.ping) { pingAge = 0; pingPhase = 0; }
                nextHit += 1;
            }
            float sample = 0;
            // Drum: a tone falling from 330 to 160 Hz with a fast decay and a short noise
            // transient — a wood-block knock rather than a bass thump ("too much bass", the
            // owner, 26 Sep).
            if (drumAge < 0.14f)
            {
                var f = (160f + 170f * MathF.Exp(-drumAge / 0.02f)) * current.pitch;
                drumPhase += 2f * MathF.PI * f * dt;
                noise = unchecked(noise * 1664525u + 1013904223u);
                var n = (float)unchecked((int)noise) / (float)int.MaxValue;
                sample += MathF.Sin(drumPhase) * MathF.Exp(-drumAge / 0.045f) * 0.9f + n * MathF.Exp(-drumAge / 0.003f) * 0.4f;
                drumAge += dt;
            }
            // Click: 4 ms of noise.
            if (clickAge < 0.006f)
            {
                noise = unchecked(noise * 1664525u + 1013904223u);
                var n = (float)unchecked((int)noise) / (float)int.MaxValue;
                sample += n * 0.6f * (1f - clickAge / 0.006f);
                clickAge += dt;
            }
            // Ping: a bright bell.
            if (pingAge < 0.35f)
            {
                pingPhase += 2f * MathF.PI * 1320f * dt;
                sample += (MathF.Sin(pingPhase) + 0.25f * MathF.Sin(2.76f * pingPhase)) * MathF.Exp(-pingAge / 0.09f) * 0.5f;
                pingAge += dt;
            }
            left[frame] = sample * gainL;
            right[frame] = sample * gainR;
        }
    }

    /// <summary>Points the beacon. <paramref name="bearing"/> is radians clockwise from screen-up.</summary>
    public void point(double bearing, BeaconAccuracy accuracy)
    {
        lock (paramsLock)
        {
            shared.active = true;
            shared.pan = (float)Math.Sin(bearing) * 0.9f;
            // North ×1.41, east and west ×1, south ×0.71: a whole octave between up and down.
            shared.pitch = (float)Math.Pow(2.0, Math.Cos(bearing) * 0.5);
            shared.accuracy = accuracy;
        }
    }

    public void silence()
    {
        lock (paramsLock) { shared.active = false; }
    }
}
