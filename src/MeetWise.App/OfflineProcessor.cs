using System.IO;
using System.Net.Http;
using MeetWise.Audio;
using MeetWise.Core.Settings;
using MeetWise.Speech;

namespace MeetWise.App;

/// <summary>
/// Runs the live microphone chain over a file, exactly as the MeetWise Microphone would (same options, 10 ms blocks at 48 kHz), so the quality
/// bench can score it. Usage:
/// <code>MeetWise.App.exe --process in.wav out.wav [--far far.wav] [--noise Max|Balanced|Light|Off] [--strength 0-100] [--voicelock [--voice sample.wav]] [--noagc] [--noecho] [--accent [--accent-strength 0-100] [--bank b.pt] [--realtime]]</code>
/// Output is 32-bit float mono at the chain's output rate. Exit code 0 on success.
/// </summary>
public static class OfflineProcessor
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        string? Value(string flag) { int i = args.ToList().FindIndex(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Count ? args[i + 1] : null; }
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        var files = args.SkipWhile(a => !a.Equals("--process", StringComparison.OrdinalIgnoreCase)).Skip(1).Take(2).ToList();
        if (files.Count < 2) { Console.Error.WriteLine("usage: --process in.wav out.wav [options]"); return 2; }

        var s = new AppSettings
        {
            NoiseCancellation = Enum.TryParse<NoiseMode>(Value("--noise"), true, out var m) ? m : NoiseMode.Max,
            NoiseStrength = int.TryParse(Value("--strength"), out var st) ? st : 100,
            EchoCancellation = !Has("--noecho") && Value("--far") is not null,
            VoiceIsolation = Has("--voicelock"),
            AutoGain = !Has("--noagc")
        };
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var options = await MicService.OptionsForAsync(http, s, s.NoiseCancellation, s.VoiceIsolation, null);
        if (s.VoiceIsolation && Value("--voice") is { } voice)
        {
            // enrol another speaker (bench): the same chain, with this voice sample instead of the trained one
            var manager = new MeetWise.Core.Models.ModelManager(http);
            var vox = MeetWise.Core.Models.ModelCatalog.VoiceIsolationFor();
            await manager.EnsureAsync(vox);
            options = options.Kind == EnhancerKind.VoiceIsolation
                ? options with { VoiceSampleWav = Path.GetFullPath(voice) }
                : new EnhancementOptions(EnhancerKind.VoiceIsolation, null, options.EchoCancellation, manager.PathFor(vox), Path.GetFullPath(voice),
                    Threads: Math.Clamp(Environment.ProcessorCount / 4, 2, 4), MinBlockMs: 80, PostKind: options.Kind, PostModelPath: options.ModelPath,
                    NoiseStrength: s.NoiseStrength, HighPass: true, AutoGain: s.AutoGain, LookAheadLimiter: true);
        }
        if (s.VoiceIsolation && options.Kind != EnhancerKind.VoiceIsolation) Console.Error.WriteLine("warning: voice lock requested but no trained voice: running without it");

        const int rate = 48000, block = 480;
        var mic = Load(files[0], rate);
        var far = Value("--far") is { } fp ? Load(fp, rate) : null;
        using var engine = new EnhancementEngine(rate, options);
        // --accent: the Accent Assist stage after the chain (the real worker). --realtime feeds blocks at the pace of the clock, as the live
        // microphone does, so late conversions fall back to the original voice exactly as they would in a call.
        AccentStage? accent = null;
        bool realtime = Has("--realtime");
        if (Has("--accent"))
        {
            var bank = Value("--bank") ?? MeetWise.Core.Models.AccentPack.Bank ?? throw new InvalidOperationException("no accent bank");
            int strength = int.TryParse(Value("--accent-strength"), out var a) ? a : 60;
            accent = new AccentStage(engine.OutputSampleRate, 300, strength / 100f, bank) { Synchronous = !realtime };
            accent.Start();
            if (!accent.WaitConnected(TimeSpan.FromMinutes(3))) { Console.Error.WriteLine("accent worker: " + accent.State); return 3; }
        }
        var output = new List<float>(mic.Length);
        var farBlock = new float[block];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i + block <= mic.Length; i += block)
        {
            if (realtime)
            {
                var due = TimeSpan.FromSeconds(i / (double)rate);
                if (due > clock.Elapsed) Thread.Sleep(due - clock.Elapsed);
            }
            float[]? f = null;
            if (far is not null)
            {
                Array.Clear(farBlock);
                if (i < far.Length) Array.Copy(far, i, farBlock, 0, Math.Min(block, far.Length - i));
                f = farBlock;
            }
            var cleaned = engine.Process(mic[i..(i + block)], f);
            if (accent is not null && cleaned.Length > 0) cleaned = accent.Process(cleaned);
            output.AddRange(cleaned);
        }
        if (accent is null) output.AddRange(engine.Flush());
        AudioFile.WriteWav(files[1], output.ToArray(), engine.OutputSampleRate);
        Console.WriteLine($"{MicService.Describe(options, s.NoiseCancellation)}; rtf {engine.RealTimeFactor:0.000}; look-ahead {engine.ModelLatencyMs:0.0} ms; errors {engine.Errors}"
            + (accent is null ? "" : $"; accent {accent.State}, delay {accent.DelayMs:0} ms, original voice used {accent.FallbackShare:P1}"));
        accent?.Dispose();
        return 0;
    }

    private static float[] Load(string path, int rate)
    {
        var (x, r) = AudioFile.ReadMono(path);
        return r == rate ? x : new StreamResampler(r, rate).Process(x);
    }
}
