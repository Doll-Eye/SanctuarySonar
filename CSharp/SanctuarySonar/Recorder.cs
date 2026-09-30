// The recorder: the game window's client area, ten frames a second, to an H.264 MP4 through
// Windows' own encoder (Media Foundation by way of MediaStreamSource + MediaTranscoder, so no
// ffmpeg and nothing to install). Beside it a .txt with the window, size, start time and marks,
// as the Mac recorder writes. The shell copies the run's log into the same folder on stop, so
// one folder holds everything a replay (CSharp/MapLab) needs.
//
// Frames come in BGRA from the shell's worker thread (`push`); the encoder pulls them on its own
// thread (`onSampleRequested`). One frame is held between the two: if the encoder falls behind,
// the older frame is dropped and counted, never queued.
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SanctuarySonar.Shell;

public sealed class Recorder
{
    public const int framesPerSecond = 10;
    public const uint bitsPerSecond = 12_000_000;
    /// <summary>Refuse to start under this much free disk; stop under `stopBelow`.</summary>
    public const long refuseBelow = 5L << 30, stopBelow = 3L << 30;

    public bool isRecording { get; private set; }
    public int width { get; private set; }
    public int height { get; private set; }
    public string? folder { get; private set; }
    public string? baseName { get; private set; }
    public DateTime startedAt { get; private set; }
    public int framesWritten { get; private set; }
    public int framesDropped { get; private set; }

    MediaStreamSource? source;
    IRandomAccessStream? stream;
    Task? transcoding;
    StreamWriter? notes;
    readonly object gate = new();
    readonly SemaphoreSlim frameReady = new(0);
    (byte[] bgra, TimeSpan time)? pending;
    bool stopping;
    static readonly TimeSpan frameDuration = TimeSpan.FromSeconds(1.0 / framesPerSecond);

    public static long freeBytes(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; } catch { return long.MaxValue; }
    }

    /// <summary>Starts a recording of `width` × `height` (both even) into `folder/baseName.mp4`.
    /// `description` goes into the notes file. Returns false, having logged why, on failure.</summary>
    public bool start(string folder, string baseName, int width, int height, string description)
    {
        if (isRecording) return false;
        try
        {
            Directory.CreateDirectory(folder);
            this.folder = folder; this.baseName = baseName; this.width = width; this.height = height;
            var path = Path.Combine(folder, baseName + ".mp4");
            Log.log($"Recording: opening {path}, {width}x{height} at {framesPerSecond} fps");

            var props = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
            props.FrameRate.Numerator = (uint)framesPerSecond; props.FrameRate.Denominator = 1;
            var descriptor = new VideoStreamDescriptor(props);
            source = new MediaStreamSource(descriptor) { BufferTime = TimeSpan.Zero };
            source.Starting += (s, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
            source.SampleRequested += onSampleRequested;

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Uhd2160p);
            profile.Audio = null;
            profile.Video.Width = (uint)width; profile.Video.Height = (uint)height;
            profile.Video.Bitrate = bitsPerSecond;
            profile.Video.FrameRate.Numerator = (uint)framesPerSecond; profile.Video.FrameRate.Denominator = 1;

            stream = FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateAlways).AsTask().GetAwaiter().GetResult();
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile).AsTask().GetAwaiter().GetResult();
            if (!prepared.CanTranscode)
            {
                Log.log($"Recording: the encoder refused ({prepared.FailureReason})");
                closeStream();
                return false;
            }
            lock (gate) { pending = null; stopping = false; framesWritten = 0; framesDropped = 0; }
            while (frameReady.CurrentCount > 0) frameReady.Wait(0);
            startedAt = DateTime.Now;
            transcoding = prepared.TranscodeAsync().AsTask();
            transcoding.ContinueWith(t => Log.log($"Recording: the encoder failed: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);

            notes = new StreamWriter(Path.Combine(folder, baseName + ".txt"), append: false) { AutoFlush = true };
            notes.WriteLine("Sanctuary Sonar recording");
            notes.WriteLine(description);
            notes.WriteLine($"Size: {width} by {height}");
            notes.WriteLine($"Frames per second: {framesPerSecond}");
            notes.WriteLine($"Started: {startedAt:yyyy-MM-dd HH:mm:ss}");
            isRecording = true;
            return true;
        }
        catch (Exception e)
        {
            Log.log($"Recording could not start: {e.Message}");
            closeStream();
            return false;
        }
    }

    /// <summary>A frame of BGRA, `width` × `height`, bottom-up (as Media Foundation reads it), `time` since the recording began.</summary>
    public void push(byte[] bgra, TimeSpan time)
    {
        lock (gate)
        {
            if (!isRecording || stopping) return;
            if (pending != null) { framesDropped++; pending = (bgra, time); return; }
            pending = (bgra, time);
        }
        frameReady.Release();
    }

    /// <summary>Notes the time in the .txt ("0:03:12.4 mark"), as the Mac recorder does.</summary>
    public void mark(TimeSpan time)
    {
        if (!isRecording) return;
        notes?.WriteLine($"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 100} mark");
    }

    /// <summary>Ends the stream and waits for the file to be finished. Returns the recording's length.</summary>
    public TimeSpan stop()
    {
        if (!isRecording) return TimeSpan.Zero;
        lock (gate) { stopping = true; }
        frameReady.Release();
        try { transcoding?.Wait(TimeSpan.FromSeconds(30)); }
        catch (Exception e) { Log.log($"Recording: finishing the file failed: {e.GetBaseException().Message}"); }
        closeStream();
        var length = DateTime.Now - startedAt;
        notes?.WriteLine($"Stopped: {DateTime.Now:yyyy-MM-dd HH:mm:ss}; {framesWritten} frames written, {framesDropped} dropped");
        notes?.Dispose(); notes = null;
        isRecording = false;
        Log.log($"Recording stopped: {framesWritten} frames written, {framesDropped} dropped, {length.TotalSeconds:F0} s");
        return length;
    }

    void onSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        try
        {
            while (true)
            {
                frameReady.Wait();
                lock (gate)
                {
                    if (pending is { } f)
                    {
                        pending = null;
                        var sample = MediaStreamSample.CreateFromBuffer(f.bgra.AsBuffer(), f.time);
                        sample.Duration = frameDuration;
                        args.Request.Sample = sample;
                        framesWritten++;
                        return;
                    }
                    if (stopping) { args.Request.Sample = null; return; }
                }
            }
        }
        catch (Exception e) { Log.log($"Recording: a frame could not be handed to the encoder: {e.Message}"); args.Request.Sample = null; }
        finally { deferral.Complete(); }
    }

    void closeStream()
    {
        try { stream?.Dispose(); } catch { }
        stream = null; source = null; transcoding = null;
    }
}

/// <summary>One frame of a recording as a BMP, through the same media pipeline (`--frame`).</summary>
public static class RecordingFrame
{
    public static string save(string mp4, double seconds, string outBmp)
    {
        var file = StorageFile.GetFileFromPathAsync(Path.GetFullPath(mp4)).AsTask().GetAwaiter().GetResult();
        var clip = Windows.Media.Editing.MediaClip.CreateFromFileAsync(file).AsTask().GetAwaiter().GetResult();
        var props = clip.GetVideoEncodingProperties();
        var composition = new Windows.Media.Editing.MediaComposition();
        composition.Clips.Add(clip);
        var thumb = composition.GetThumbnailAsync(TimeSpan.FromSeconds(seconds), (int)props.Width, (int)props.Height, Windows.Media.Editing.VideoFramePrecision.NearestFrame).AsTask().GetAwaiter().GetResult();
        var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(thumb).AsTask().GetAwaiter().GetResult();
        var pixels = decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            new Windows.Graphics.Imaging.BitmapTransform(), Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult().DetachPixelData();
        Bmp.write(outBmp, pixels, (int)decoder.PixelWidth, (int)decoder.PixelHeight);
        return $"{props.Subtype} {props.Width}x{props.Height}, {clip.OriginalDuration.TotalSeconds:F1} s, {props.FrameRate.Numerator}/{props.FrameRate.Denominator} fps; frame at {seconds} s -> {outBmp} ({decoder.PixelWidth}x{decoder.PixelHeight})";
    }
}
