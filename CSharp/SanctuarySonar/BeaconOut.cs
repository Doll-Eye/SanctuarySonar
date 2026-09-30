// The beacon on Windows: BeaconSynth (the port of the Swift render block) pulled by NAudio's
// WASAPI shared-mode output at 48 kHz stereo float.
using NAudio.Wave;

namespace SanctuarySonar.Shell;

public sealed class BeaconOut : IBeacon, ISampleProvider, IDisposable
{
    readonly BeaconSynth synth = new();
    WasapiOut? output;
    float[] left = new float[4096], right = new float[4096];

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    public BeaconOut() { synth.volume = Settings.getDouble("beaconVolume", 0.7); }

    public void point(double bearing, BeaconAccuracy accuracy) => synth.point(bearing, accuracy);
    public void silence() => synth.silence();
    public double volume
    {
        get => synth.volume;
        set { synth.volume = value; Settings.set("beaconVolume", synth.volume); }
    }

    public void startEngine()
    {
        if (output != null && output.PlaybackState == PlaybackState.Playing) return;
        try
        {
            output?.Dispose();
            output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 40);
            output.Init(this);
            output.Play();
        }
        catch (Exception e) { Log.log($"Beacon engine did not start: {e.Message}"); }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        if (left.Length < frames) { left = new float[frames]; right = new float[frames]; }
        synth.render(left, right, frames, WaveFormat.SampleRate);
        for (int i = 0; i < frames; i++) { buffer[offset + i * 2] = left[i]; buffer[offset + i * 2 + 1] = right[i]; }
        return frames * 2;
    }

    public void Dispose() { output?.Dispose(); output = null; }
}
