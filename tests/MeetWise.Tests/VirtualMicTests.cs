using MeetWise.Audio;
using MeetWise.Core.Models;
using NAudio.CoreAudioApi;

namespace MeetWise.Tests;

public class VirtualMicTests
{
    private static EndpointInfo Cap(string id, string name, string adapter) => new(id, name, adapter, DataFlow.Capture);

    // ---- recognising the virtual cable and avoiding feedback ----

    [Theory]
    [InlineData("VB-Audio Virtual Cable", true)]
    [InlineData("vb-audio virtual cable", true)]
    [InlineData("USB Advanced Audio Device", false)]
    [InlineData("NVIDIA Broadcast", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsVirtualAdapter_UsesTheAdapterName_NotTheEndpointName(string? adapter, bool expected)
        => Assert.Equal(expected, VirtualMic.IsVirtualAdapter(adapter));

    private static readonly EndpointInfo Usb = Cap("usb", "Microphone (USB Advanced Audio Device)", "USB Advanced Audio Device");
    private static readonly EndpointInfo Broadcast = Cap("nv", "Microphone (NVIDIA Broadcast)", "NVIDIA Broadcast");
    // renamed by MeetWise: the endpoint name no longer says "CABLE", but the adapter name still identifies it
    private static readonly EndpointInfo Virtual = Cap("vb", "MeetWise Microphone (VB-Audio Virtual Cable)", "VB-Audio Virtual Cable");

    [Fact]
    public void ChoosePhysicalMic_KeepsARealDefault()
        => Assert.Equal("usb", VirtualMic.ChoosePhysicalMic("default", "usb", new[] { Usb, Virtual, Broadcast }, null));

    [Fact]
    public void ChoosePhysicalMic_NeverReturnsTheVirtualMic_EvenWhenItIsTheWindowsDefault()
    {
        // the user made MeetWise Microphone the Windows default: cleaning "the default" would record our own output
        Assert.Equal("nv", VirtualMic.ChoosePhysicalMic("default", "vb", new[] { Virtual, Usb, Broadcast }, "nv"));
        Assert.Equal("usb", VirtualMic.ChoosePhysicalMic("default", "vb", new[] { Virtual, Usb, Broadcast }, null)); // falls back to the first real one
        Assert.Equal("usb", VirtualMic.ChoosePhysicalMic("default", "vb", new[] { Virtual, Usb }, "unplugged-device"));
        Assert.NotEqual("vb", VirtualMic.ChoosePhysicalMic("vb", "vb", new[] { Virtual, Usb }, null)); // even if asked for explicitly
        Assert.Null(VirtualMic.ChoosePhysicalMic("default", "vb", new[] { Virtual }, null));         // nothing real to use
    }

    [Fact]
    public void ChoosePhysicalMic_HonoursAnExplicitRealChoice()
        => Assert.Equal("nv", VirtualMic.ChoosePhysicalMic("nv", "usb", new[] { Usb, Broadcast, Virtual }, "usb"));

    // ---- against the real Windows audio system ----

    [Fact]
    public void RealDevices_VirtualMicIsNeverOfferedAsASource()
    {
        var mics = new AudioDeviceService().GetMicrophones();
        Assert.Equal("default", mics[0].Id);
        foreach (var m in mics.Skip(1))
            Assert.DoesNotContain("VB-Audio", VirtualMic.Endpoints(NAudio.CoreAudioApi.DataFlow.Capture).First(e => e.Id == m.Id).Adapter);
        using var en = new MMDeviceEnumerator();
        using var opened = AudioDeviceService.OpenCapture("default", en); // must resolve to a real device
        Assert.False(VirtualMic.IsVirtualAdapter(VirtualMic.AdapterOf(opened)));
    }

    [Fact]
    public void RealDevices_PropertyStoreInterop_ReadsTheSameNameAsNAudio()
    {
        // read-only check that our COM wrapper (used for renaming) is wired correctly; nothing is written
        var any = VirtualMic.Endpoints(DataFlow.Capture).FirstOrDefault();
        if (any is null) return;
        using var en = new MMDeviceEnumerator();
        using var device = en.GetDevice(any.Id);
        var expected = device.Properties[PropertyKeys.PKEY_Device_DeviceDesc].Value?.ToString();
        Assert.Equal(expected, EndpointProperties.ReadDescription(any.Id));
    }

    [Fact]
    public void RealDevices_SettingTheDefaultToItself_ChangesNothing()
    {
        var before = DefaultMicrophone.Current();
        if (before.Communications is null) return;
        Assert.True(DefaultMicrophone.Set(before.Communications));
        // restoring each role to what it was is a no-op too, and proves the round trip
        Assert.True(DefaultMicrophone.Restore(before));
        Assert.Equal(before, DefaultMicrophone.Current());
    }

    // NOTE: no test writes an endpoint name. EndpointProperties.WriteDescription changes the name of a real Windows audio device
    // (it succeeds even without elevation on some PCs), so it is only ever called by the installer helper on the VB-CABLE endpoints.

    // ---- installer decisions (no driver is installed by these tests) ----

    [Theory]
    [InlineData(VirtualMicInstaller.ExitOk, true, InstallOutcome.Installed)]
    [InlineData(VirtualMicInstaller.ExitOk, false, InstallOutcome.NeedsReboot)]
    [InlineData(VirtualMicInstaller.ExitNotVisible, false, InstallOutcome.NeedsReboot)]
    [InlineData(VirtualMicInstaller.ExitRenameFailed, true, InstallOutcome.Installed)]
    [InlineData(VirtualMicInstaller.ExitSetupMissing, false, InstallOutcome.Failed)]
    [InlineData(VirtualMicInstaller.ExitSetupFailed, false, InstallOutcome.Failed)]
    [InlineData(99, false, InstallOutcome.Failed)]
    public void Interpret_TurnsHelperExitCodesIntoAdvice(int code, bool visible, InstallOutcome expected)
    {
        var r = VirtualMicInstaller.Interpret(code, visible);
        Assert.Equal(expected, r.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(r.Message));
    }

    private static readonly HttpClient NoNetwork = new(new StubHandler());

    [Fact]
    public async Task Install_DoesNothing_WhenAlreadyInstalled()
    {
        bool downloaded = false, elevated = false;
        var r = await VirtualMicInstaller.InstallAsync(NoNetwork, endpointsVisible: () => true,
            ensurePack: () => { downloaded = true; return Task.CompletedTask; }, runElevated: _ => { elevated = true; return Task.FromResult(0); });
        Assert.Equal(InstallOutcome.AlreadyInstalled, r.Outcome);
        Assert.False(downloaded);
        Assert.False(elevated);
    }

    [Fact]
    public async Task Install_ReportsCancelled_WhenTheUserDeclinesTheWindowsPrompt()
    {
        var r = await VirtualMicInstaller.InstallAsync(NoNetwork, endpointsVisible: () => false, waitSeconds: 0,
            ensurePack: () => Task.CompletedTask, runElevated: _ => throw new System.ComponentModel.Win32Exception(1223));
        Assert.Equal(InstallOutcome.Cancelled, r.Outcome);
    }

    [Fact]
    public async Task Install_ReportsADownloadFailure_WithoutAskingForAdministratorRights()
    {
        bool elevated = false;
        var r = await VirtualMicInstaller.InstallAsync(NoNetwork, endpointsVisible: () => false,
            ensurePack: () => throw new HttpRequestException("offline"), runElevated: _ => { elevated = true; return Task.FromResult(0); });
        Assert.Equal(InstallOutcome.Failed, r.Outcome);
        Assert.Contains("offline", r.Message);
        Assert.False(elevated);
    }

    [Fact]
    public async Task Install_RunsTheElevatedHelperOnce_AndWaitsForTheDevicesToAppear()
    {
        var calls = new List<string>();
        int checks = 0;
        var r = await VirtualMicInstaller.InstallAsync(NoNetwork,
            endpointsVisible: () => ++checks > 3, // not there at first, visible a moment after the driver finished
            ensurePack: () => Task.CompletedTask, runElevated: arg => { calls.Add(arg); return Task.FromResult(VirtualMicInstaller.ExitOk); }, waitSeconds: 10);
        Assert.Equal(new[] { VirtualMicInstaller.InstallArg }, calls);
        Assert.Equal(InstallOutcome.Installed, r.Outcome);
    }

    [Fact]
    public async Task Install_SaysRestart_WhenWindowsNeverShowsTheDevice()
    {
        var r = await VirtualMicInstaller.InstallAsync(NoNetwork, endpointsVisible: () => false, waitSeconds: 0,
            ensurePack: () => Task.CompletedTask, runElevated: _ => Task.FromResult(VirtualMicInstaller.ExitNotVisible));
        Assert.Equal(InstallOutcome.NeedsReboot, r.Outcome);
        Assert.Contains("restart", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pack_RealDownload_PassesHashCheck_AndContainsTheSetupProgram()
    {
        // downloads the official 1.3 MB pack from vb-audio.com, verifies the pinned SHA-256, unpacks it; installs nothing
        var root = Path.Combine(Path.GetTempPath(), "mw-vbpack-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        string setup;
        try { setup = await VirtualCablePack.EnsureAsync(http, modelsRoot: Path.Combine(root, "models"), targetDir: Path.Combine(root, "pack")); }
        catch (HttpRequestException) { return; } // offline machine: nothing to verify
        Assert.True(File.Exists(setup));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(setup)!, "vbMmeCable64_win10.inf")));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(setup)!, "vbaudio_cable64_win10.sys")));
        Assert.Contains("vb-cable.com", VirtualCablePack.Notice);
        Assert.Contains("donationware", VirtualCablePack.Notice);
    }

    // ---- start with Windows ----

    [Fact]
    public void StartupRegistration_AddsAndRemovesTheRunEntry_ForTheCurrentUserOnly()
    {
        var name = "MeetWiseTest-" + Guid.NewGuid().ToString("N");
        var reg = new StartupRegistration(name);
        try
        {
            Assert.False(reg.IsEnabled);
            reg.Enable(@"C:\Program Files\MeetWise\MeetWise.App.exe");
            Assert.True(reg.IsEnabled);
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")!;
            Assert.Equal("\"C:\\Program Files\\MeetWise\\MeetWise.App.exe\" --minimized", key.GetValue(name));
            reg.Set(false, "ignored");
            Assert.False(reg.IsEnabled);
        }
        finally { reg.Disable(); }
    }
}
