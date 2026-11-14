using MeetWise.Audio;
using MeetWise.Core.Settings;
using NAudio.CoreAudioApi;

namespace MeetWise.Tests;

/// <summary>NVIDIA Broadcast cleans the microphone; MeetWise only adds what Broadcast does not do.</summary>
public class BroadcastModeTests
{
    private static readonly EndpointInfo Cable = new("vb", "MeetWise Microphone", "VB-Audio Virtual Cable", DataFlow.Capture);
    private static readonly EndpointInfo Usb = new("usb", "Microphone", "USB Advanced Audio Device", DataFlow.Capture);
    private static readonly EndpointInfo Broadcast = new("nv", "Microphone", "NVIDIA Broadcast", DataFlow.Capture);

    [Fact]
    public void FindsTheBroadcastMicrophone_NeverTheCable()
    {
        Assert.Equal("nv", VirtualMic.FindBroadcastMic(new[] { Cable, Usb, Broadcast })!.Id);
        Assert.Null(VirtualMic.FindBroadcastMic(new[] { Cable, Usb }));
        Assert.True(VirtualMic.IsBroadcast(new EndpointInfo("x", "Microphone (NVIDIA Broadcast)", "", DataFlow.Capture)));
    }

    [Fact]
    public void BroadcastOptions_SwitchOffMeetWisesOwnCleanup()
    {
        var s = new AppSettings { Cleanup = CleanupEngine.NvidiaBroadcast, NoiseCancellation = NoiseMode.Max, VoiceIsolation = true, EchoCancellation = false, AutoGain = true };
        var o = MicService.BroadcastOptions(s);
        Assert.Null(o.Kind);            // no denoiser
        Assert.Null(o.PostKind);        // no voice lock / second stage
        Assert.False(o.HighPass);
        Assert.False(o.EchoCancellation);
        Assert.True(o.AutoGain);
        Assert.True(o.LookAheadLimiter);
        Assert.True(MicService.BroadcastOptions(new AppSettings { EchoCancellation = true }).EchoCancellation); // speaker echo stays available
    }

    [Fact]
    public void CleanupChoice_SurvivesSaveAndLoad_AndDefaultsToMeetWise()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-bc-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(dir);
        Assert.Equal(CleanupEngine.MeetWise, store.Load().Cleanup);
        var s = store.Load();
        s.Cleanup = CleanupEngine.NvidiaBroadcast;
        store.Save(s);
        Assert.Equal(CleanupEngine.NvidiaBroadcast, new SettingsStore(dir).Load().Cleanup);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"settingsVersion\": 2, \"cleanup\": 7 }");
        Assert.Equal(CleanupEngine.MeetWise, new SettingsStore(dir).Load().Cleanup); // an unknown value falls back
    }
}
