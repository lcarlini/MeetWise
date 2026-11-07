using System.IO;
using MeetWise.Audio;
using MeetWise.Core;
using MeetWise.Core.Hardware;
using MeetWise.Core.Models;
using MeetWise.Intelligence;
using MeetWise.Speech;
using System.Net.Http;
using MeetWise.Storage;

namespace MeetWise.App;

/// <summary>Loads every native dependency and reports what works on this PC. Exit code 0 means all required parts loaded.</summary>
public static class SelfTest
{
    public static int Run(string? reportPath)
    {
        var lines = new List<string>();
        int failures = 0;

        void Check(string name, Func<string> body, bool required = true)
        {
            try { lines.Add($"PASS  {name}: {body()}"); }
            catch (Exception ex)
            {
                lines.Add($"{(required ? "FAIL" : "WARN")}  {name}: {ex.GetType().Name}: {ex.Message}");
                if (required) failures++;
            }
        }

        lines.Add($"MeetWise self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss} on {Environment.OSVersion}");
        Check("Hardware", () =>
        {
            var hw = HardwareDetector.Detect();
            return $"tier {hw.Tier}, {hw.CpuThreads} threads, GPU {(hw.Gpus.Count > 0 ? hw.Gpus[0].Name + " " + hw.Gpus[0].VramMb + " MB" : "none")}, Whisper {ModelCatalog.WhisperFor(hw).Id}";
        });
        Check("SQLite + full-text search", () =>
        {
            var db = new MeetingDatabase(Path.Combine(Path.GetTempPath(), "mw-selftest-" + Guid.NewGuid().ToString("N"), "t.db"));
            var m = db.AddMeeting("selftest", DateTime.UtcNow);
            db.AddSegment(m, "Me", 0, 1, "hello selftest");
            return db.Search("selftest").Count == 1 ? "ok" : throw new InvalidOperationException("search returned nothing");
        });
        Check("Audio devices (WASAPI)", () =>
        {
            var svc = new AudioDeviceService();
            return $"{svc.GetMicrophones().Count - 1} microphone(s), {svc.GetRenderDevices().Count} output(s)";
        });
        Check("Speech engine (sherpa-onnx)", () =>
        {
            using var mgr = new SherpaOnnx.SpeakerEmbeddingManager(192); // loads the native library without needing a model
            return "native library loaded";
        });
        Check("Whisper runtime", () =>
        {
            var gpu = CudaRuntimePack.Activate();
            var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
            if (!Directory.Exists(runtimes) || Directory.GetFiles(runtimes, "whisper.dll", SearchOption.AllDirectories).Length == 0)
                throw new FileNotFoundException("whisper.dll is missing from the build");
            var model = Directory.Exists(Path.Combine(AppPaths.Models, "whisper")) ? Directory.GetFiles(Path.Combine(AppPaths.Models, "whisper"), "*.bin").FirstOrDefault() : null;
            if (model is null) return $"runtime files present (GPU pack {(gpu ? "installed" : "not installed")}); load not tested because no model is downloaded yet";
            using var factory = Whisper.net.WhisperFactory.FromPath(model);
            return $"{Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary} runtime loaded (GPU pack {(gpu ? "installed" : "not installed")})";
        });
        Check("llama.cpp runtime", () =>
        {
            LlamaBackend.Configure(CudaRuntimePack.Activate());
            if (!LLama.Native.NativeLibraryConfig.All.DryRun(out var lib, out _) || lib is null)
                throw new InvalidOperationException("no llama.cpp backend could be loaded");
            return lib is { Metadata.UseCuda: true } ? "CUDA backend" : "CPU backend";
        });
        Check("Windows speech voices", () => $"{WindowsVoice.InstalledLanguages.Count} language(s): {string.Join(", ", WindowsVoice.InstalledLanguages.Take(8))}", required: false);
        Check("FFmpeg", () => FFmpegPack.Find() ?? throw new FileNotFoundException("not downloaded yet (fetched on first video recording)"), required: false);
        Check("Monitors", () => string.Join("; ", new MonitorService().GetMonitors().Select(m => m.ToString())), required: false);
        Check("yt-dlp (YouTube import)", () => YtDlpPack.Find() ?? throw new FileNotFoundException("not downloaded yet (fetched on first YouTube import)"), required: false);
        Check("MeetWise Microphone (virtual cable)", () =>
        {
            var mic = VirtualMic.FindMic();
            var feed = VirtualMic.FindFeed();
            if (mic is null || feed is null) throw new InvalidOperationException("not installed: use \"Create MeetWise Microphone\" in the app");
            var desc = EndpointProperties.ReadDescription(mic.Id);
            var isDefault = DefaultMicrophone.Current().Communications == mic.Id;
            return $"\"{mic.Name}\" (named {desc}) and \"{feed.Name}\"; Windows default: {(isDefault ? "yes" : "no")}";
        }, required: false);
        Check("Voice profile", () => VoiceProfile.IsTrained()
            ? $"trained ({VoiceProfile.Load()?.Sentences} sentences)"
            : throw new InvalidOperationException("not trained yet: use \"Train my voice\" in the app"), required: false);
        Check("English coach models", () =>
        {
            var m = new ModelManager(new HttpClient());
            return m.IsInstalled(ModelCatalog.PunctuationEnglish) && m.IsInstalled(ModelCatalog.SileroVad) ? "punctuation and voice-activity models present"
                : throw new FileNotFoundException("downloaded on first use");
        }, required: false);
        Check("Virtual audio device", () =>
        {
            var v = new AudioDeviceService().GetRenderDevices().Where(d => AudioDeviceService.LooksVirtual(d.Name)).Select(d => d.Name).ToList();
            return v.Count > 0 ? string.Join("; ", v) : throw new InvalidOperationException("none found: install VB-CABLE to let meeting apps hear the cleaned microphone");
        }, required: false);

        lines.Add(failures == 0 ? "RESULT: all required components loaded" : $"RESULT: {failures} required component(s) failed");
        var text = string.Join(Environment.NewLine, lines);
        try { File.WriteAllText(reportPath ?? Path.Combine(AppPaths.Roaming, "logs", "selftest.txt"), text); } catch (Exception) { /* report is best effort */ }
        return failures == 0 ? 0 : 1;
    }
}
