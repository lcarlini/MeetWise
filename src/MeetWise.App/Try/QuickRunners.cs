using System.IO;
using MeetWise.Audio;
using MeetWise.Core;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;
using MeetWise.Speech;
using NAudio.CoreAudioApi;

namespace MeetWise.App.Try;

/// <summary>The work behind each "Test with my settings" box. Every runner reads the page's current settings, never its own.</summary>
internal static class QuickRunners
{
    private static TryContext Ctx(QuickInput i, string text = "", string language = "en")
        => new() { Mic = i.Clip?.Mic ?? Array.Empty<float>(), Loopback = i.Clip?.Loopback, Text = text, Language = language, Status = i.Status };

    private static string Lines(IEnumerable<TryRow> rows) => string.Join("\n\n", rows.Select(r => r.Label + "\n" + r.Details));

    /// <summary>The same physical microphone the live clean-up route uses.</summary>
    public static string PhysicalMic(AppSettings s)
        => VirtualMic.ChoosePhysicalMic(s.MicrophoneId, VirtualMic.WindowsDefaultCaptureId(), VirtualMic.Endpoints(DataFlow.Capture), s.PhysicalMicId) ?? s.MicrophoneId;

    // ---- MeetWise Microphone: the exact live chain ----

    public static async Task<QuickResult> Microphone(StudioContext ctx, QuickInput input)
    {
        var s = ctx.Settings;
        var clip = input.Clip!;
        input.Status.Report(Loc.T("cmp.applying"));
        var options = await EnhancementController.OptionsForAsync(ctx.Http, s, s.NoiseCancellation, s.VoiceIsolation,
            new Progress<double>(p => input.Status.Report(Loc.T("try.r.downloading", p.ToString("P0")))));
        var mic = clip.Mic;
        float[]? far = null;
        if (options.EchoCancellation && clip.Loopback is { Length: > 0 } lb)
        {
            int n = Math.Min(mic.Length, lb.Length);
            mic = mic[..n]; far = lb[..n];
        }
        var r = await Task.Run(() => TryRunners.RunEngine(mic, far, options));
        var c = AudioMetrics.Compare(mic, 16000, r.Samples, r.Rate);
        string applied = EnhancementController.Describe(options, s.NoiseCancellation);
        string echoNote = options.EchoCancellation && far is null ? "\n" + Loc.T("cmp.echo.idle") : "";
        string lockNote = options.Kind == EnhancerKind.VoiceIsolation ? "\n" + Loc.T("cmp.voicelock.note") : "";
        string summary = Loc.T("cmp.applied", applied) + "\n" + Loc.T("cmp.result", c.NoiseRemovedDb.ToString("0"), c.VoiceChangeDb.ToString("+0.0;-0.0;0.0"), r.Rtf.ToString("P0")) + echoNote + lockNote;
        return new QuickResult(summary, mic, 16000, r.Samples, r.Rate);
    }

    // ---- English coach ----

    public static async Task<QuickResult> Coach(StudioContext ctx, QuickInput input)
    {
        var rows = await TryRunners.Coach(ctx).Run(Ctx(input, input.Text));
        return new QuickResult(Loc.T("quick.coach.done"), input.Clip?.Mic, 16000, null, 16000, Lines(rows));
    }

    // ---- Record page: what the recording will capture, and its subtitles ----

    public static async Task<QuickResult> RecordPreview(StudioContext ctx, QuickInput input, string language, bool toEnglish, bool cleaned)
    {
        var clip = input.Clip!;
        var (text, secs) = await TryRunners.Whisper(ctx, Ctx(input), clip.Mic, language, "try.r.whisper", toEnglish);
        string where = Loc.T(cleaned ? "quick.rec.cleaned" : "quick.rec.raw", cleaned ? ctx.Enhance.Applied : "");
        string summary = where + "\n" + Loc.T("quick.rec.subs", Languages.Name(language, Loc.Instance.Language), toEnglish ? Loc.T("quick.rec.english") : "", secs.ToString("0.0"));
        // the clip is what the recording captures: it is both "original" and "with my settings" (the cleaning, if any, happened before it)
        return new QuickResult(summary, clip.Mic, 16000, null, 16000, text.Length == 0 ? Loc.T("try.r.nospeech") : text);
    }

    // ---- Library: preview an import with the real FFmpeg noise filter ----

    public static async Task<QuickResult> ImportPreview(StudioContext ctx, QuickInput input, bool noiseFilter, string language, bool toEnglish)
    {
        var file = input.FilePath!;
        input.Status.Report(Loc.T("try.loading"));
        var ffmpeg = await FFmpegPack.EnsureAsync(ctx.Http, new Progress<double>(p => input.Status.Report(Loc.T("try.r.downloading", p.ToString("P0")))));
        var dir = Path.Combine(Path.GetTempPath(), "mw-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            async Task<float[]> Extract(string name, string? filter)
            {
                var wav = Path.Combine(dir, name);
                var af = filter is null ? "" : $" -af \"{filter}\"";
                var (code, output) = await FFmpegPack.RunAsync(ffmpeg, $"-hide_banner -loglevel error -y -i \"{file}\" -t 30 -vn -ac 1 -ar 16000{af} \"{wav}\"", 120_000);
                if (code != 0 || !File.Exists(wav)) throw new InvalidOperationException(Loc.T("try.cantread", output.Trim()));
                return AudioFile.ReadMono16k(wav);
            }
            var original = await Extract("o.wav", null);
            float[]? filtered = noiseFilter ? await Extract("f.wav", MediaImporter.NoiseFilter) : null;
            var heard = filtered ?? original;
            var (text, secs) = await TryRunners.Whisper(ctx, Ctx(input), heard, language, "try.r.whisper", toEnglish);
            string what = Loc.T(noiseFilter ? "quick.imp.filter" : "quick.imp.nofilter");
            string summary = Loc.T("quick.imp.summary", Path.GetFileName(file), (original.Length / 16000.0).ToString("0")) + "\n" + what;
            if (filtered is not null)
            {
                var c = AudioMetrics.Compare(original, 16000, filtered, 16000);
                summary += " " + Loc.T("quick.imp.change", c.NoiseRemovedDb.ToString("0"), c.VoiceChangeDb.ToString("+0.0;-0.0;0.0"));
            }
            return new QuickResult(summary, original, 16000, filtered, 16000, text.Length == 0 ? Loc.T("try.r.nospeech") : text);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ---- AI Assistant ----

    public static async Task<QuickResult> AssistantSample(StudioContext ctx, QuickInput input)
    {
        var rows = await TryRunners.Assistant(ctx).Run(Ctx(input));
        return new QuickResult(Loc.T("quick.ai.done"), Details: Lines(rows));
    }

    // ---- Settings: live translation ----

    public static async Task<QuickResult> Translation(StudioContext ctx, QuickInput input, string target)
    {
        var rows = await TryRunners.Translate(ctx).Run(Ctx(input, input.Text, target));
        var spoken = rows.FirstOrDefault(r => r.Samples is not null);
        return new QuickResult(Loc.T("quick.tr.done"), null, 16000, spoken?.Samples, spoken?.Rate ?? 16000, Lines(rows.Where(r => r.Samples is null)));
    }
}
