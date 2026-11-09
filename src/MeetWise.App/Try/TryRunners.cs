using System.Diagnostics;
using System.IO;
using MeetWise.Audio;
using MeetWise.Core;
using MeetWise.Core.Models;
using MeetWise.Intelligence;
using MeetWise.Speech;

namespace MeetWise.App.Try;

/// <summary>The tests behind every "Try it" panel. Each one runs the real feature on the clip and reports numbers a person can judge.</summary>
internal static class TryRunners
{
    private static string D(double v) => v.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture);
    private static string N(double v, string f = "0.0") => v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);

    // ======================= microphone clean-up =======================

    public sealed record CleanResult(float[] Samples, int Rate, double Rtf, double P99Ms, EchoStats? Echo);

    /// <summary>Runs a clip through the same engine the live microphone uses, in 10 ms blocks, and times every block.</summary>
    public static CleanResult RunEngine(float[] mic, float[]? far, EnhancementOptions options, int blockSamples = 160)
    {
        using var engine = new EnhancementEngine(16000, options);
        var output = new List<float>(mic.Length);
        var times = new List<double>(mic.Length / 160 + 1);
        var sw = new Stopwatch();
        for (int i = 0; i < mic.Length; i += blockSamples)
        {
            int n = Math.Min(blockSamples, mic.Length - i);
            var block = mic.AsSpan(i, n).ToArray();
            float[]? farBlock = far is not null && far.Length >= i + n ? far.AsSpan(i, n).ToArray() : null;
            sw.Restart();
            var y = engine.Process(block, farBlock);
            times.Add(sw.Elapsed.TotalMilliseconds);
            output.AddRange(y);
        }
        output.AddRange(engine.Flush());
        return new CleanResult(output.ToArray(), engine.OutputSampleRate, engine.RealTimeFactor, AudioMetrics.Percentile(times, 0.99), engine.EchoStats);
    }

    private static string Verdict(double rtf) => rtf < 0.5 ? Loc.T("try.r.fast") : rtf < 0.9 ? Loc.T("try.r.tight") : Loc.T("try.r.slow");

    private static TryRow Described(string label, ClipComparison c, CleanResult r)
        => new(label, Loc.T("try.r.metrics", D(-c.NoiseRemovedDb), D(c.VoiceChangeDb), N(c.OriginalSnrDb, "0"), N(c.ProcessedSnrDb, "0"), r.Rtf.ToString("P0"), Verdict(r.Rtf)),
            r.Samples, r.Rate);

    [Flags]
    public enum CleanSet { Noise = 1, Echo = 2, VoiceLock = 4, All = 7 }

    public static TryTest Cleanup(StudioContext ctx, CleanSet set, string nameKey = "try.t.cleanup", string tipKey = "tip.try.t.cleanup")
        => new(nameKey, tipKey, TryInput.Audio, c => RunCleanup(ctx, c, set));

    private static async Task<IReadOnlyList<TryRow>> RunCleanup(StudioContext ctx, TryContext c, CleanSet set)
    {
        var rows = new List<TryRow>();
        var (n0, s0) = AudioMetrics.Profile(c.Mic, 16000);
        rows.Add(new TryRow(Loc.T("try.r.original"), Loc.T("try.r.original.details", N(n0), N(s0), N(s0 - n0, "0")), c.Mic, 16000));
        var manager = new ModelManager(ctx.Http);

        async Task Noise(string labelKey, EnhancerKind kind, ModelEntry model)
        {
            c.Status.Report(Loc.T("try.r.preparing", Loc.T(labelKey)));
            await manager.EnsureAsync(model);
            c.Status.Report(Loc.T("try.r.running", Loc.T(labelKey)));
            var opts = new EnhancementOptions(kind, manager.PathFor(model), false);
            var r = await Task.Run(() => RunEngine(c.Mic, null, opts), c.Ct);
            rows.Add(Described(Loc.T(labelKey), AudioMetrics.Compare(c.Mic, 16000, r.Samples, r.Rate), r));
        }

        if (set.HasFlag(CleanSet.Noise))
        {
            await Noise("try.r.light", EnhancerKind.Light, ModelCatalog.DenoiseLight);
            await Noise("try.r.balanced", EnhancerKind.FullBand, ModelCatalog.DenoiseBalanced);
            await Noise("try.r.max", EnhancerKind.FullBand, ModelCatalog.DenoiseFullBand);
        }

        if (set.HasFlag(CleanSet.Echo))
        {
            if (c.Loopback is { Length: > 8000 } far)
            {
                c.Status.Report(Loc.T("try.r.running", Loc.T("try.r.echo")));
                int len = Math.Min(c.Mic.Length, far.Length);
                var mic = c.Mic[..len];
                var r = await Task.Run(() => RunEngine(mic, far[..len], new EnhancementOptions(null, null, true)), c.Ct);
                var st = r.Echo;
                string locked = st is { DelayLocked: true } ? Loc.T("try.r.locked") : Loc.T("try.r.notlocked");
                rows.Add(new TryRow(Loc.T("try.r.echo"),
                    Loc.T("try.r.echo.details", N(AudioMetrics.ErleDb(mic, r.Samples)), N(st?.DelayMs ?? 0, "0"), locked, r.Rtf.ToString("P0")),
                    r.Samples, r.Rate));
            }
            else rows.Add(new TryRow(Loc.T("try.r.echo"), Loc.T("try.r.noecho")));
        }

        if (set.HasFlag(CleanSet.VoiceLock))
        {
            if (VoiceProfile.IsTrained())
            {
                c.Status.Report(Loc.T("try.r.preparing", Loc.T("try.r.voicelock")));
                await manager.EnsureAsync(ModelCatalog.VoiceIsolation);
                c.Status.Report(Loc.T("try.r.running", Loc.T("try.r.voicelock")));
                var dir = manager.PathFor(ModelCatalog.VoiceIsolation);
                var opts = new EnhancementOptions(EnhancerKind.VoiceIsolation, null, false, dir, VoiceProfile.ClipPath(), Math.Clamp(Environment.ProcessorCount / 4, 2, 4), MinBlockMs: 80);
                var r = await Task.Run(() => RunEngine(c.Mic, null, opts), c.Ct);
                rows.Add(Described(Loc.T("try.r.voicelock"), AudioMetrics.Compare(c.Mic, 16000, r.Samples, r.Rate), r));
                foreach (var (key, model) in new[] { ("try.r.voicelock.balanced", ModelCatalog.DenoiseBalanced), ("try.r.voicelock.max", ModelCatalog.DenoiseFullBand) })
                {
                    await manager.EnsureAsync(model);
                    c.Status.Report(Loc.T("try.r.running", Loc.T(key)));
                    var chained = opts with { PostKind = EnhancerKind.FullBand, PostModelPath = manager.PathFor(model) };
                    var rc = await Task.Run(() => RunEngine(c.Mic, null, chained), c.Ct);
                    rows.Add(Described(Loc.T(key), AudioMetrics.Compare(c.Mic, 16000, rc.Samples, rc.Rate), rc));
                }
            }
            else rows.Add(new TryRow(Loc.T("try.r.voicelock"), Loc.T("try.r.novoicelock")));
        }
        return rows;
    }

    // ======================= transcription =======================

    private static float[] To16k(float[] x, int rate) => rate == 16000 ? x : new Resampler(rate, 1).Process(x);

    internal static async Task<(string Text, double Seconds)> Whisper(StudioContext ctx, TryContext c, float[] samples, string language, string statusKey, bool translateToEnglish = false)
    {
        c.Status.Report(Loc.T("try.r.preparing", Loc.T(statusKey)));
        await ctx.OfferGpuPackAsync(c.Status);
        CudaRuntimePack.Activate(); // makes the CUDA DLLs findable, so Whisper runs on the graphics card when the GPU pack is installed
        ctx.Llm.Release(); // Whisper and the 8B language model do not fit in 8 GB together
        var manager = new ModelManager(ctx.Http);
        var entry = ModelCatalog.WhisperFor(ctx.Hardware);
        await manager.EnsureAsync(entry, new Progress<double>(p => c.Status.Report(Loc.T("try.r.downloading", p.ToString("P0")))));
        c.Status.Report(Loc.T("try.r.running", Loc.T(statusKey)));
        using var whisper = new WhisperTranscriber(manager.PathFor(entry));
        var sw = Stopwatch.StartNew();
        var segments = await whisper.TranscribeAsync(samples, language, ctx.Settings.Glossary, ct: c.Ct, translateToEnglish: translateToEnglish);
        return (string.Join(" ", segments.Select(s => s.Text.Trim())).Trim(), sw.Elapsed.TotalSeconds);
    }

    private static TryRow Heard(string label, string text, double seconds, double clipSeconds, string reference)
    {
        string wer = reference.Length == 0 ? "" : Loc.T("try.r.wer", AudioMetrics.WordErrorRate(reference, text).ToString("P0"));
        return new TryRow(label, Loc.T("try.r.text", text.Length == 0 ? "—" : text, N(seconds), (seconds / Math.Max(clipSeconds, 0.1)).ToString("P0"), wer));
    }

    public static TryTest Transcribe(StudioContext ctx) => new("try.t.transcribe", "tip.try.t.transcribe", TryInput.AudioAndText, async c =>
    {
        var rows = new List<TryRow>();
        double clipSeconds = c.Mic.Length / 16000.0;
        string lang = c.Language;
        var manager = new ModelManager(ctx.Http);

        if (lang is "en" or "auto")
        {
            c.Status.Report(Loc.T("try.r.preparing", Loc.T("try.r.stream")));
            await manager.EnsureAsync(ModelCatalog.StreamingEnglish);
            var finals = new List<string>();
            double firstWord = -1;
            var sw = Stopwatch.StartNew();
            await Task.Run(() =>
            {
                using var rec = new StreamingRecognizer(manager.PathFor(ModelCatalog.StreamingEnglish), "Me", 16000, 1);
                rec.Transcript += e =>
                {
                    if (firstWord < 0 && e.Text.Length > 0) firstWord = sw.Elapsed.TotalSeconds;
                    if (e.IsFinal && e.Text.Length > 0) finals.Add(e.Text);
                };
                for (int i = 0; i < c.Mic.Length; i += 1600) rec.Feed(c.Mic.AsSpan(i, Math.Min(1600, c.Mic.Length - i)));
                rec.Finish();
            }, c.Ct);
            rows.Add(Heard(Loc.T("try.r.stream"), string.Join(" ", finals), sw.Elapsed.TotalSeconds, clipSeconds, c.Text));
        }

        var (text, secs) = await Whisper(ctx, c, c.Mic, lang, "try.r.whisper");
        rows.Add(Heard(Loc.T("try.r.whisper"), text, secs, clipSeconds, c.Text));

        // does cleaning the audio first help the recogniser?
        var bal = ModelCatalog.DenoiseBalanced;
        await manager.EnsureAsync(bal);
        var clean = await Task.Run(() => RunEngine(c.Mic, null, new EnhancementOptions(EnhancerKind.FullBand, manager.PathFor(bal), false)), c.Ct);
        var (text2, secs2) = await Whisper(ctx, c, To16k(clean.Samples, clean.Rate), lang, "try.r.whisper.clean");
        rows.Add(Heard(Loc.T("try.r.whisper.clean"), text2, secs2, clipSeconds, c.Text));
        return rows;
    }, NeedsLanguage: true, TextHintKey: "try.hint.reference", DefaultLanguage: Languages.Auto);

    // ======================= speakers =======================

    public static TryTest Speakers(StudioContext ctx) => new("try.t.speakers", "tip.try.t.speakers", TryInput.Audio, async c =>
    {
        var manager = new ModelManager(ctx.Http);
        c.Status.Report(Loc.T("try.r.preparing", Loc.T("try.t.speakers")));
        await manager.EnsureAsync(ModelCatalog.DiarizationSegmentation);
        await manager.EnsureAsync(ModelCatalog.SpeakerEmbedding);
        c.Status.Report(Loc.T("try.r.running", Loc.T("try.t.speakers")));
        var sw = Stopwatch.StartNew();
        var rows = await Task.Run(() =>
        {
            using var diarizer = new Diarizer(Path.Combine(manager.PathFor(ModelCatalog.DiarizationSegmentation), "model.onnx"), manager.PathFor(ModelCatalog.SpeakerEmbedding));
            using var profiles = new SpeakerProfiles(manager.PathFor(ModelCatalog.SpeakerEmbedding), Path.Combine(AppPaths.Roaming, "voices", "voices.json"));
            var diar = diarizer.Diarize(c.Mic);
            var list = new List<TryRow>();
            if (diar.Count == 0) { list.Add(new TryRow(Loc.T("try.t.speakers"), Loc.T("try.r.nospeech"))); return list; }
            var names = SpeakerLabeling.DiarizedNames(diar, c.Mic, profiles);
            foreach (var g in diar.GroupBy(d => d.Speaker).OrderBy(g => g.Min(d => d.Start)))
            {
                string turns = string.Join(", ", g.Take(6).Select(d => $"{TimeSpan.FromSeconds(d.Start):mm\\:ss}–{TimeSpan.FromSeconds(d.End):mm\\:ss}"));
                list.Add(new TryRow(names[g.Key], Loc.T("try.r.speaker", N(g.Sum(d => d.End - d.Start)), g.Count(), turns + (g.Count() > 6 ? " …" : ""))));
            }
            return list;
        }, c.Ct);
        rows.Add(new TryRow(Loc.T("try.r.time"), Loc.T("try.r.analysed", N(sw.Elapsed.TotalSeconds))));
        return rows;
    });

    // ======================= translation =======================

    public static TryTest Translate(StudioContext ctx) => new("try.t.translate", "tip.try.t.translate", TryInput.AudioOrText, async c =>
    {
        var rows = new List<TryRow>();
        string text = c.Text;
        if (text.Length == 0)
        {
            var (heard, _) = await Whisper(ctx, c, c.Mic, "auto", "try.r.whisper");
            text = heard;
        }
        if (text.Length == 0) return new[] { new TryRow(Loc.T("try.r.src"), Loc.T("try.r.nospeech")) };
        rows.Add(new TryRow(Loc.T("try.r.src"), text));

        string target = c.Language == Languages.Auto ? "en" : c.Language;
        c.Status.Report(Loc.T("try.r.preparing", Loc.T("try.r.ai")));
        var llm = await ctx.GetLlmAsync(c.Status);
        c.Status.Report(Loc.T("try.r.running", Loc.T("try.t.translate")));
        var translator = new LlmTranslator(llm, new TranslatorOptions(Glossary: ctx.Settings.Glossary));
        var sw = Stopwatch.StartNew();
        var result = await translator.TranslateDetailedAsync(text, target, null, c.Ct);
        long ms = sw.ElapsedMilliseconds;
        string extra = (result.Warnings.Count > 0 ? "\n⚠ " + string.Join("; ", result.Warnings) : "") + (result.UsedFallback ? "\n" + Loc.T("try.r.fallback") : "");
        rows.Add(new TryRow(Loc.T("try.r.tr", Languages.Name(target, Loc.Instance.Language)), result.Text + "\n" + Loc.T("try.r.ms", ms) + extra));

        try
        {
            using var voice = new WindowsVoice();
            if (voice.SelectLanguage(target))
            {
                var (samples, rate) = await voice.SynthesizeAsync(result.Text, c.Ct);
                rows.Add(new TryRow(Loc.T("try.r.spoken"), Loc.T("try.r.voice", voice.CurrentVoice), samples, rate));
            }
            else rows.Add(new TryRow(Loc.T("try.r.spoken"), Loc.T("try.r.novoice", Languages.Name(target, Loc.Instance.Language))));
        }
        catch (Exception ex) { rows.Add(new TryRow(Loc.T("try.r.spoken"), Loc.T("try.r.novoice", ex.Message))); }
        return rows;
    }, NeedsLanguage: true, TextHintKey: "try.hint.translate", DefaultLanguage: ctx.Settings.CaptionTranslateTo ?? "pt");

    // ======================= English coach =======================

    public static TryTest Coach(StudioContext ctx) => new("try.t.coach", "tip.try.t.coach", TryInput.AudioOrText, async c =>
    {
        string text = c.Text;
        double? wpm = null;
        if (text.Length == 0)
        {
            var (heard, _) = await Whisper(ctx, c, c.Mic, "en", "try.r.whisper");
            text = heard;
            double words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            wpm = words / Math.Max(c.Mic.Length / 16000.0 / 60.0, 1e-3);
        }
        if (text.Length == 0) return new[] { new TryRow(Loc.T("try.r.you"), Loc.T("try.r.nospeech")) };

        c.Status.Report(Loc.T("try.r.preparing", Loc.T("try.r.ai")));
        var llm = await ctx.GetLlmAsync(c.Status);
        c.Status.Report(Loc.T("try.r.running", Loc.T("try.t.coach")));
        var sw = Stopwatch.StartNew();
        var fb = await new SpeechCoach(llm).ReviewAsync(text, wpm, 0, c.Ct);
        long ms = sw.ElapsedMilliseconds;
        var rows = new List<TryRow> { new(Loc.T("try.r.you"), text + (wpm is { } w ? "\n" + Loc.T("try.r.pace", N(w, "0")) : "")) };
        if (fb.Ok || fb.Issues.Count == 0) rows.Add(new TryRow(Loc.T("try.r.fix"), Loc.T("try.r.ok") + "\n" + Loc.T("try.r.ms", ms)));
        else
        {
            rows.Add(new TryRow(Loc.T("try.r.fix"), fb.Corrected + "\n" + Loc.T("try.r.ms", ms)));
            foreach (var i in fb.Issues) rows.Add(new TryRow($"{i.Original} → {i.Suggestion}", $"{i.Type}: {i.Why}"));
        }
        return rows;
    });

    // ======================= AI assistant on a sample meeting =======================

    private static readonly AssistantLine[] SampleMeeting =
    {
        new(0, "Ana", "Thanks for joining. Today we need to decide the launch date for the mobile app."),
        new(8, "Bruno", "QA found three blocking bugs in the payment screen. I think we need until next Friday to fix them."),
        new(19, "Ana", "That works. Carla, can marketing be ready by then?"),
        new(25, "Carla", "Yes, the campaign is ready. We only need the final screenshots from design by Wednesday."),
        new(35, "Ana", "Great. So the decision is: launch on the twentieth, if the payment bugs are fixed."),
        new(44, "Bruno", "I will send the bug status every morning. The risk is the payment provider's sandbox, it was down twice this week."),
        new(55, "Ana", "Carla, please book the press call. Bruno, you own the bug fixes. We meet again Monday at ten.")
    };

    public static TryTest Assistant(StudioContext ctx) => new("try.t.assistant", "tip.try.t.assistant", TryInput.None, async c =>
    {
        c.Status.Report(Loc.T("try.r.preparing", Loc.T("try.r.ai")));
        var llm = await ctx.GetLlmAsync(c.Status);
        var assistant = new TranscriptAssistant(llm);
        var rows = new List<TryRow> { new(Loc.T("try.r.sample"), string.Join("\n", SampleMeeting.Select(l => $"{l.Speaker}: {l.Text}"))) };
        foreach (var (cmd, key) in new[] { (AssistantCommand.Summarize, "try.r.summary"), (AssistantCommand.ActionItems, "try.r.actions"), (AssistantCommand.Chapters, "try.r.chapters") })
        {
            c.Status.Report(Loc.T("try.r.running", Loc.T(key)));
            var sw = Stopwatch.StartNew();
            var answer = await assistant.RunAsync(SampleMeeting, new AssistantRequest(cmd, Language: Loc.Instance.Language), ct: c.Ct);
            rows.Add(new TryRow(Loc.T(key), answer.Trim() + "\n" + Loc.T("try.r.ms", sw.ElapsedMilliseconds)));
        }
        return rows;
    });

    // ======================= video encoders =======================

    public static TryTest Encoders(StudioContext ctx) => new("try.t.encoders", "tip.try.t.encoders", TryInput.None, async c =>
    {
        c.Status.Report(Loc.T("try.r.preparing", "FFmpeg"));
        var ffmpeg = await FFmpegPack.EnsureAsync(ctx.Http, new Progress<double>(p => c.Status.Report(Loc.T("try.r.downloading", p.ToString("P0")))));
        c.Status.Report(Loc.T("try.r.running", Loc.T("try.t.encoders")));
        var sw = Stopwatch.StartNew();
        var list = await VideoRecorder.ProbeEncodersAsync(ffmpeg);
        var rows = new List<TryRow>();
        for (int i = 0; i < list.Count; i++)
            rows.Add(new TryRow(list[i].Name + (i == 0 ? "  ✓ " + Loc.T("try.r.chosen") : ""), Loc.T("try.r.codec", list[i].Codec)));
        if (rows.Count == 0) rows.Add(new TryRow(Loc.T("try.t.encoders"), Loc.T("try.r.noenc")));
        rows.Add(new TryRow(Loc.T("try.r.time"), Loc.T("try.r.analysed", N(sw.Elapsed.TotalSeconds))));
        return rows;
    });
}
