using MeetWise.Core.Hardware;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;

namespace MeetWise.Tests;

public class CoreTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "mw-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Settings_RoundTrip_And_Defaults()
    {
        var dir = Temp();
        var store = new SettingsStore(dir);
        Assert.Equal(NoiseMode.Max, store.Load().NoiseCancellation);
        Assert.True(store.Load().AlwaysOnMicrophone);
        var s = store.Load();
        s.NoiseCancellation = NoiseMode.Light;
        s.NoiseStrength = 40;
        store.Save(s);
        var back = new SettingsStore(dir).Load();
        Assert.Equal(NoiseMode.Light, back.NoiseCancellation);
        Assert.Equal(40, back.NoiseStrength);
    }

    [Fact]
    public void Settings_CorruptFile_FallsBackToDefaults()
    {
        var dir = Temp();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ not json");
        Assert.Equal(NoiseMode.Max, new SettingsStore(dir).Load().NoiseCancellation);
    }

    [Theory]
    [InlineData(8188, true, HardwareTier.A_NvidiaRtx)]
    [InlineData(4096, true, HardwareTier.B_AnyGpu)]
    public void Tier_Classification(long vram, bool nvidia, HardwareTier expected)
        => Assert.Equal(expected, HardwareDetector.ClassifyTier(new GpuInfo("g", vram, nvidia), true));

    [Fact]
    public void Tier_NoGpu_IsCpuOnly() => Assert.Equal(HardwareTier.C_CpuOnly, HardwareDetector.ClassifyTier(null, false));

    [Theory]
    [InlineData(8188, "large-v3-turbo")]
    [InlineData(6000, "medium")]
    [InlineData(0, "base")]
    public void WhisperModel_ByVram(long vram, string expected) => Assert.Equal(expected, HardwareDetector.SelectWhisperModel(vram));

    [Fact]
    public async Task ModelManager_VerifiesChecksum()
    {
        var dir = Temp();
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "src.bin");
        await File.WriteAllBytesAsync(src, new byte[] { 1, 2, 3 });
        var hash = await ModelManager.Sha256Async(src);
        var mm = new ModelManager(new HttpClient(), Path.Combine(dir, "models"));

        var bad = new ModelEntry("m.bin", "asr", new Uri(src).AbsoluteUri, new string('0', 64), 3);
        await Assert.ThrowsAnyAsync<Exception>(() => mm.EnsureAsync(bad));
        Assert.False(mm.IsInstalled(bad));
    }
}

public class ArchiveTests
{
    [Fact]
    public void ExtractTarBz2_ExtractsFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-arc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var archive = Path.Combine(dir, "a.tar.bz2");
        var py = "import tarfile,io;t=tarfile.open(r'" + archive + "','w:bz2');d=b'hello';i=tarfile.TarInfo('m/tokens.txt');i.size=len(d);t.addfile(i,io.BytesIO(d));t.close()";
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("python", new[] { "-c", py }) { UseShellExecute = false });
        p!.WaitForExit();
        if (!File.Exists(archive)) return; // python unavailable
        MeetWise.Core.Models.ModelManager.ExtractTarBz2(archive, Path.Combine(dir, "out"));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dir, "out", "m", "tokens.txt")));
    }
}
