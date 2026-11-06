using MeetWise.Speech;

namespace MeetWise.Tests;

public class WhisperTests
{
    private static string Models => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models");

    [Fact]
    public async Task Whisper_TranscribesEnglish_AndDetectsLanguage()
    {
        var model = Path.Combine(Models, "whisper", "ggml-base.bin");
        if (!File.Exists(model)) return;
        bool gpuPack = MeetWise.Core.Models.CudaRuntimePack.Activate();
        var wav = Path.Combine(Models, "asr", "sherpa-onnx-streaming-zipformer-en-2023-06-26", "test_wavs", "0.wav");
        var audio = MeetWise.Audio.AudioFile.ReadMono16k(wav);
        var log = new List<string>();
        using var logSub = Whisper.net.Logger.LogProvider.AddLogger((level, message) => log.Add(message ?? ""));
        using var w = new WhisperTranscriber(model);
        var segs = await w.TranscribeAsync(audio, "auto");
        var text = string.Join(" ", segs.Select(s => s.Text)).ToUpperInvariant();
        // With the GPU pack installed the CUDA runtime must be the one in use (the library loads once per process, so check the state, not a log line).
        if (gpuPack) Assert.Equal(Whisper.net.LibraryLoader.RuntimeLibrary.Cuda, Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary);
        Assert.Contains("NIGHTFALL", text);
        Assert.Contains("BROTHELS", text);
        Assert.Equal("en", segs[0].Language);
        Assert.True(segs[^1].End > 5);
    }
}

public class CudaPackTests
{
    [Fact]
    public void ExtractDlls_PullsOnlyRequestedLibraries_FromNestedArchiveFolders()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-cuda-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, "a.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var n in new[] { "pkg/bin/cublas64_13.dll", "pkg/bin/cublasLt64_13.dll", "pkg/bin/nvblas64_13.dll", "pkg/include/cublas.h" })
            {
                using var w = new StreamWriter(zip.CreateEntry(n).Open());
                w.Write("x");
            }
        }
        var target = Path.Combine(dir, "out");
        Directory.CreateDirectory(target);
        MeetWise.Core.Models.CudaRuntimePack.ExtractDlls(zipPath, target, new[] { "cublas64_13.dll", "cublasLt64_13.dll" });
        Assert.Equal(new[] { "cublas64_13.dll", "cublasLt64_13.dll" }, Directory.GetFiles(target).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public void IsInstalled_RequiresAllLibraries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-cuda-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Assert.False(MeetWise.Core.Models.CudaRuntimePack.IsInstalled(dir));
        foreach (var d in MeetWise.Core.Models.CudaRuntimePack.RequiredDlls) File.WriteAllText(Path.Combine(dir, d), "x");
        Assert.True(MeetWise.Core.Models.CudaRuntimePack.IsInstalled(dir));
    }
}

public class RefinerTests
{
    private static string Models => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models");

    [Fact]
    public async Task Refine_ReplacesLiveTranscript_WithPerChannelWhisperText()
    {
        var model = Path.Combine(Models, "whisper", "ggml-base.bin");
        var wavs = Path.Combine(Models, "asr", "sherpa-onnx-streaming-zipformer-en-2023-06-26", "test_wavs");
        if (!File.Exists(model) || !Directory.Exists(wavs)) return;
        MeetWise.Core.Models.CudaRuntimePack.Activate();

        var db = new MeetWise.Storage.MeetingDatabase(Path.Combine(Path.GetTempPath(), "mw-rf-" + Guid.NewGuid().ToString("N"), "t.db"));
        var m = db.AddMeeting("Call", DateTime.UtcNow);
        db.AddSegment(m, "Me", 0, 3, "live draft that is wrong");

        var silent = Path.Combine(Path.GetTempPath(), "mw-silent.wav");
        using (var w = new NAudio.Wave.WaveFileWriter(silent, new NAudio.Wave.WaveFormat(16000, 16, 1)))
            w.WriteSamples(new float[16000 * 3], 0, 16000 * 3);

        using var whisper = new MeetWise.Speech.WhisperTranscriber(model);
        int n = await MeetWise.Audio.MeetingRefiner.RefineAsync(db, m, Path.Combine(wavs, "0.wav"), Path.Combine(wavs, "1.wav"), whisper, "en");
        var t = db.GetTranscript(m);

        Assert.Equal(t.Count, n);
        Assert.DoesNotContain(t, s => s.Text.Contains("live draft"));
        Assert.Contains(t, s => s.Speaker == "Me" && s.Text.ToUpperInvariant().Contains("NIGHTFALL"));
        Assert.Contains(t, s => s.Speaker == "Others" && s.Text.ToUpperInvariant().Contains("CONSEQUENCE"));
        Assert.Equal(t.OrderBy(s => s.StartSec).Select(s => s.Id), t.Select(s => s.Id));
        Assert.Single(db.Search("nightfall")); // full-text index followed the replacement

        // A silent remote channel must not produce hallucinated text, and an empty result must not wipe the transcript.
        int none = await MeetWise.Audio.MeetingRefiner.RefineAsync(db, m, silent, null, whisper, "en");
        Assert.Equal(0, none);
        Assert.Equal(t.Count, db.GetTranscript(m).Count);
    }

    [Fact]
    public void WhisperModel_ByTier()
    {
        MeetWise.Core.Hardware.HardwareProfile Hw(MeetWise.Core.Settings.HardwareTier t, int threads) => new(Array.Empty<MeetWise.Core.Hardware.GpuInfo>(), threads, t, "x");
        Assert.Equal("ggml-large-v3-turbo-q5_0.bin", MeetWise.Core.Models.ModelCatalog.WhisperFor(Hw(MeetWise.Core.Settings.HardwareTier.A_NvidiaRtx, 16)).Id);
        Assert.Equal("ggml-small-q5_1.bin", MeetWise.Core.Models.ModelCatalog.WhisperFor(Hw(MeetWise.Core.Settings.HardwareTier.B_AnyGpu, 8)).Id);
        Assert.Equal("ggml-base.bin", MeetWise.Core.Models.ModelCatalog.WhisperFor(Hw(MeetWise.Core.Settings.HardwareTier.C_CpuOnly, 4)).Id);
    }
}
