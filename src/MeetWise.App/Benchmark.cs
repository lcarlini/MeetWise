using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using MeetWise.App.Try;
using MeetWise.Audio;
using MeetWise.Core;
using MeetWise.Core.Hardware;
using MeetWise.Core.Logging;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;
using MeetWise.Intelligence;
using MeetWise.Speech;
using MeetWise.Storage;

namespace MeetWise.App;

/// <summary>
/// <c>MeetWise.App.exe --benchmark [clip.wav] [--quick] [--out report.md]</c>: runs the same tests as the "Try it" panels, without a window, and writes a
/// Markdown report. Without a clip it speaks three sentences with a Windows voice and adds hum and hiss, so the text is known and word errors can be counted.
/// --quick skips the tests that need the AI language model.
/// </summary>
internal static class Benchmark
{
    public static async Task<int> RunAsync(string? clipPath, bool quick, string? outPath, bool tune = false, string? dumpDir = null)
    {
        var report = new StringBuilder();
        void Line(string s = "") { report.AppendLine(s); AppLog.Info("benchmark: " + s); }
        int failures = 0;
        try
        {
            var store = new SettingsStore();
            var settings = store.Load();
            var hardware = HardwareDetector.Detect(settings.TierOverride);
            var http = new HttpClient();
            var db = new MeetingDatabase(Path.Combine(Path.GetTempPath(), "mw-bench-" + Guid.NewGuid().ToString("N"), "b.db"));
            var secrets = new SecretStore();
            var ctx = new StudioContext
            {
                Store = store, Settings = settings, Hardware = hardware, Db = db, Http = http, Secrets = secrets,
                Llm = new LlmFactory(http, secrets), Session = new RecordingSession(db, http, hardware), Enhance = new EnhancementController(http),
                Imports = ImportService.Create(db, http, hardware, () => settings.Glossary), NeverAskGpuPack = true
            };

            Line($"# MeetWise benchmark {DateTime.Now:yyyy-MM-dd HH:mm}");
            Line();
            var gpu = hardware.Gpus.Count > 0 ? $"{hardware.Gpus[0].Name} ({hardware.Gpus[0].VramMb / 1024.0:0.#} GB)" : "no dedicated GPU";
            Line($"PC: {gpu}, {hardware.CpuThreads} threads, tier {hardware.Tier}, GPU pack {(CudaRuntimePack.IsInstalled() ? "installed" : "not installed")}");

            float[] mic; string reference = ""; string source;
            if (clipPath is not null) { mic = TryClip.FromFile(clipPath).Mic; source = Path.GetFileName(clipPath); }
            else (mic, reference, source) = await Task.Run(MakeTestClip);
            Directory.CreateDirectory(TryClip.Folder);
            TryClip.Save(Path.Combine(TryClip.Folder, "benchmark-clip.wav"), mic); // so the same clip can be opened in the Try panels
            Line($"Clip: {source}, {mic.Length / 16000.0:0.0} s");
            Line();

            var tests = new List<TryTest>
            {
                TryRunners.Cleanup(ctx, TryRunners.CleanSet.Noise | TryRunners.CleanSet.VoiceLock),
                TryRunners.Transcribe(ctx), TryRunners.Speakers(ctx), TryRunners.Encoders(ctx)
            };
            if (!quick) tests.AddRange(new[] { TryRunners.Translate(ctx), TryRunners.Coach(ctx), TryRunners.Assistant(ctx) });

            if (tune)
            {
                // how many CPU threads voice lock needs to keep up in real time on this PC
                Line("## Voice lock tuning (threads)");
                var manager = new ModelManager(http);
                await manager.EnsureAsync(ModelCatalog.VoiceIsolation);
                var dir = manager.PathFor(ModelCatalog.VoiceIsolation);
                foreach (int threads in new[] { 2, 4, 8 })
                    foreach (int block in new[] { 160, 320, 640, 1600 })
                    {
                        var r = await Task.Run(() => TryRunners.RunEngine(mic, null, new EnhancementOptions(EnhancerKind.VoiceIsolation, null, false, dir, VoiceProfile.ClipPath(), threads), block));
                        Line($"- {threads} thread(s), {block / 16} ms blocks: {r.Rtf:P0} of real time, slowest 1 % of blocks {r.P99Ms:0.0} ms");
                    }
                Line();
            }

            foreach (var test in tests)
            {
                var sw = Stopwatch.StartNew();
                Line($"## {Loc.Get(test.NameKey, "en")}");
                try
                {
                    var tc = new TryContext
                    {
                        Mic = mic, Text = test.Input is TryInput.AudioAndText ? reference : test.Input is TryInput.Text or TryInput.AudioOrText ? (reference.Length > 0 ? reference : "") : "",
                        Language = test.NeedsLanguage ? (test.NameKey == "try.t.translate" ? "pt" : "en") : "en", Status = new Progress<string>(_ => { })
                    };
                    foreach (var row in await test.Run(tc))
                    {
                        Line($"- **{row.Label}**: {row.Details.Replace("\r", "").Replace("\n", " / ")}");
                        if (dumpDir is not null && row.Samples is { Length: > 0 } audio)
                        {
                            // every result as a WAV, to listen to or to feed back into --benchmark
                            Directory.CreateDirectory(dumpDir);
                            var name = new string(row.Label.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-').ToArray()).Trim('-');
                            TryClip.Save(Path.Combine(dumpDir, name + ".wav"), row.Rate == 16000 ? audio : new Resampler(row.Rate, 1).Process(audio));
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures++;
                    Line($"- FAILED: {ex.GetType().Name}: {ex.Message}");
                    AppLog.Error("Benchmark test failed: " + test.NameKey, ex);
                }
                Line($"_total {sw.Elapsed.TotalSeconds:0.0} s_");
                Line();
            }
        }
        catch (Exception ex)
        {
            failures++;
            Line("FAILED: " + ex);
        }

        var path = outPath ?? Path.Combine(TryClip.Folder, $"benchmark-{DateTime.Now:yyyyMMdd-HHmm}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, report.ToString(), new UTF8Encoding(false));
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Three spoken sentences (Windows voice) with a 50 Hz hum and hiss added, as 16 kHz mono, plus the text that was spoken.</summary>
    private static (float[] Clip, string Reference, string Source) MakeTestClip()
    {
        var sentences = VoiceTrainer.Prompts.Take(3).ToArray();
        using var voice = new WindowsVoice();
        if (!voice.SelectLanguage("en")) throw new InvalidOperationException("No English Windows voice is installed to make the test clip. Pass a clip with --benchmark clip.wav.");
        var speech = new List<float>();
        var gap = new float[8000];
        foreach (var s in sentences)
        {
            var (samples, rate) = voice.SynthesizeAsync(s).GetAwaiter().GetResult();
            speech.AddRange(rate == 16000 ? samples : new Resampler(rate, 1).Process(samples));
            speech.AddRange(gap);
        }
        var x = speech.ToArray();
        double peak = x.Max(Math.Abs);
        var rnd = new Random(7);
        for (int i = 0; i < x.Length; i++)
        {
            double voiceSample = x[i] / Math.Max(peak, 1e-6) * 0.25;
            double hiss = (rnd.NextDouble() * 2 - 1) * 0.03;
            double hum = 0.02 * Math.Sin(2 * Math.PI * 50 * i / 16000.0);
            x[i] = (float)(voiceSample + hiss + hum);
        }
        return (x, string.Join(" ", sentences), "synthetic: 3 spoken sentences + hum and hiss");
    }
}
