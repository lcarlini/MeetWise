using MeetWise.Audio;

namespace MeetWise.Tests;

/// <summary>Runs against the real Windows audio devices. Skipped automatically when the machine has none.</summary>
public class DeviceTests
{
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
