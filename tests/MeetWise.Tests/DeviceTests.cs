using MeetWise.Audio;

namespace MeetWise.Tests;

/// <summary>Runs against the real Windows audio devices. Skipped automatically when the machine has none.</summary>
public class DeviceTests
{
    [Fact]
    public async Task Recorder_CapturesBothChannels_FromRealDevices()
    {
        var svc = new AudioDeviceService();
        if (svc.GetMicrophones().Count < 2 || !svc.IsSystemAudioAvailable()) return;

        var dir = Path.Combine(Path.GetTempPath(), "mw-dev-" + Guid.NewGuid().ToString("N"));
        using var rec = new DualChannelRecorder();
        int micBuffers = 0, loopBuffers = 0;
        rec.DataAvailable += (src, fmt, buf, n) => { if (src == "mic") Interlocked.Increment(ref micBuffers); else Interlocked.Increment(ref loopBuffers); };
        rec.Start(dir, "t", "default");
        Assert.NotNull(rec.MicFormat);
        Assert.NotNull(rec.LoopbackFormat);
        await Task.Delay(2500);
        rec.Stop();

        Assert.True(micBuffers > 20, $"mic delivered only {micBuffers} buffers");
        // Loopback only delivers data while something plays; the file must still be a valid wav.
        using var mic = new NAudio.Wave.WaveFileReader(rec.MicPath!);
        Assert.InRange(mic.TotalTime.TotalSeconds, 1.5, 4.0);
        using var loop = new NAudio.Wave.WaveFileReader(rec.LoopbackPath!);
        Assert.True(loop.TotalTime.TotalSeconds >= 0);
        Assert.False(rec.IsRecording);
    }

    [Fact]
    public void Route_CanBeConstructedAndDisposed_WithoutAudibleOutput()
    {
        var svc = new AudioDeviceService();
        if (svc.GetRenderDevices().Count == 0 || svc.GetMicrophones().Count < 2) return;
        using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        using var mic = AudioDeviceService.OpenCapture("default", en);
        using var render = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        // Constructing (not starting) proves format negotiation and graph setup work on this hardware without playing the mic back.
        using var route = new AudioRoute(mic, render, new MeetWise.Speech.EnhancementOptions(null, null, false));
        Assert.Equal(0, route.Overflows);
    }
}
