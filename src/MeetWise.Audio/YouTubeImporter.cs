using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeetWise.Core;
using MeetWise.Core.Models;
using MeetWise.Speech;

namespace MeetWise.Audio;

public enum YouTubeFailure { Blocked, Unavailable, NeedsJavaScriptRuntime, NoTranscriber, Other }

public sealed class YouTubeException : Exception
{
    public YouTubeFailure Kind { get; }
    public YouTubeException(YouTubeFailure kind, string message) : base(message) => Kind = kind;
}

public sealed record YouTubeInfo(string Id, string Title, double DurationSeconds, string? Language, IReadOnlyList<string> ManualCaptions, IReadOnlyList<string> AutoCaptions);

public sealed record CaptionChoice(string Code, bool IsManual, bool FellBack);

public enum TranscriptSource { OfficialCaptions, AutoCaptions, LocalTranscription }

public sealed record YouTubeResult(IReadOnlyList<SubtitleCue> Cues, string TxtPath, string? SrtPath, YouTubeInfo Info, TranscriptSource Source, string? Language, string? Notice);

public static partial class YouTubeUrl
{
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")] private static partial Regex IdRegex();

    /// <summary>The 11-character video id from a watch, youtu.be, shorts, embed or live link, or from a bare id. Null for anything else (including playlists).</summary>
    public static string? ExtractId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();
        if (IdRegex().IsMatch(input)) return input;
        if (!Uri.TryCreate(input.Contains("://") ? input : "https://" + input, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host.ToLowerInvariant();
        string? candidate = null;
        if (host == "youtu.be") candidate = uri.AbsolutePath.Trim('/').Split('/')[0];
        else if (host == "youtube.com" || host.EndsWith(".youtube.com") || host.EndsWith(".youtube-nocookie.com") || host == "youtube-nocookie.com")
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            candidate = query["v"];
            if (candidate is null)
            {
                var parts = uri.AbsolutePath.Trim('/').Split('/');
                if (parts.Length >= 2 && parts[0] is "shorts" or "embed" or "live" or "v") candidate = parts[1];
            }
        }
        return candidate is not null && IdRegex().IsMatch(candidate) ? candidate : null;
    }
}

/// <summary>
/// Subtitles from a YouTube link, captions first: official captions, then auto-generated ones, in the chosen language when possible. When the video has
/// none (or a translation to English is wanted and unavailable), the audio is downloaded and transcribed locally.
/// </summary>
public sealed class YouTubeImporter
{
    private readonly string _ytdlp;
    private readonly MediaImporter? _audio;
    private readonly Func<string, Task<(int ExitCode, string Output)>> _run;
    private readonly Func<bool> _hasJsRuntime;

    public YouTubeImporter(string ytdlpExe, MediaImporter? audioImporter, Func<string, Task<(int ExitCode, string Output)>>? run = null, Func<bool>? hasJavaScriptRuntime = null)
    {
        _ytdlp = ytdlpExe;
        _audio = audioImporter;
        _run = run ?? (args => FFmpegPack.RunAsync(ytdlpExe, args, 10 * 60_000));
        _hasJsRuntime = hasJavaScriptRuntime ?? YtDlpPack.HasJavaScriptRuntime;
    }

    // ---- choosing captions ----

    /// <summary>
    /// Official captions in the wanted language, then auto-generated ones, then (when the language is "auto") the video's own language and finally any
    /// official track. When English is wanted from another language, YouTube's English tracks (including its auto-translation) are used if present.
    /// </summary>
    public static CaptionChoice? PickCaption(YouTubeInfo info, string language, bool translateToEnglish = false)
    {
        if (translateToEnglish) language = "en";
        var wanted = language == Languages.Auto ? null : Languages.YouTubeCodes(language);

        if (wanted is not null)
        {
            var manual = Match(info.ManualCaptions, wanted);
            if (manual is not null) return new CaptionChoice(manual, true, false);
            var auto = Match(info.AutoCaptions, wanted);
            if (auto is not null) return new CaptionChoice(auto, false, false);
            if (translateToEnglish) return null; // fall back to local translation of the audio
        }
        var own = info.Language is null ? null : Languages.YouTubeCodes(info.Language.Split('-')[0]);
        if (own is not null)
        {
            var manual = Match(info.ManualCaptions, own);
            if (manual is not null) return new CaptionChoice(manual, true, wanted is not null);
            var auto = Match(info.AutoCaptions, own);
            if (auto is not null) return new CaptionChoice(auto, false, wanted is not null);
        }
        if (info.ManualCaptions.Count > 0) return new CaptionChoice(info.ManualCaptions[0], true, wanted is not null);
        return null;
    }

    private static string? Match(IReadOnlyList<string> available, IReadOnlyList<string> wanted)
    {
        foreach (var code in wanted)
        {
            var hit = available.FirstOrDefault(a => a.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        // "pt" also matches "pt-BR" style tracks and the reverse
        foreach (var code in wanted)
        {
            var hit = available.FirstOrDefault(a => a.StartsWith(code + "-", StringComparison.OrdinalIgnoreCase) || code.StartsWith(a + "-", StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }

    public static YouTubeInfo ParseInfo(string json)
    {
        var root = JsonNode.Parse(json[json.IndexOf('{')..])!.AsObject();
        static IReadOnlyList<string> Keys(JsonNode? n) => n is JsonObject o ? o.Select(kv => kv.Key).Where(k => k != "live_chat").ToList() : new List<string>();
        double.TryParse(root["duration"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration);
        return new YouTubeInfo(root["id"]?.ToString() ?? "", root["title"]?.ToString() ?? "YouTube video", duration, root["language"]?.ToString(),
            Keys(root["subtitles"]), Keys(root["automatic_captions"]));
    }

    public static YouTubeFailure Classify(string output)
    {
        var o = output.ToLowerInvariant();
        if (o.Contains("not a bot") || o.Contains("429") || o.Contains("too many requests") || o.Contains("sign in to confirm")) return YouTubeFailure.Blocked;
        if (o.Contains("unavailable") || o.Contains("private video") || o.Contains("members-only") || o.Contains("removed")) return YouTubeFailure.Unavailable;
        if (o.Contains("javascript runtime") || o.Contains("n challenge") || o.Contains("deno") && o.Contains("required")) return YouTubeFailure.NeedsJavaScriptRuntime;
        return YouTubeFailure.Other;
    }

    public static string Message(YouTubeFailure kind, string detail) => kind switch
    {
        YouTubeFailure.Blocked => "YouTube blocked the request. Wait a minute and try the same link again.",
        YouTubeFailure.Unavailable => "This video is unavailable.",
        YouTubeFailure.NeedsJavaScriptRuntime => "YouTube downloads need Node.js or Deno. Install Node.js from https://nodejs.org and try again.",
        YouTubeFailure.NoTranscriber => "This video has no captions, and no local transcription model is ready.",
        _ => "YouTube import failed: " + detail.Trim()
    };

    // ---- import ----

    public async Task<YouTubeInfo> InfoAsync(string videoId)
    {
        var (code, output) = await _run($"--dump-single-json --skip-download --no-playlist --no-warnings \"https://www.youtube.com/watch?v={videoId}\"");
        if (code != 0) { var kind = Classify(output); throw new YouTubeException(kind, Message(kind, output)); }
        return ParseInfo(output);
    }

    public async Task<YouTubeResult> ImportAsync(string urlOrId, string outputFolder, ImportOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var id = YouTubeUrl.ExtractId(urlOrId) ?? throw new ArgumentException("That is not a YouTube video link.", nameof(urlOrId));
        var info = await InfoAsync(id);
        var work = Path.Combine(Path.GetTempPath(), "meetwise-yt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string? notice = null;
            var choice = PickCaption(info, options.Language, options.TranslateToEnglish);
            if (choice is not null)
            {
                var cues = await FetchCaptionsAsync(id, choice, work);
                if (cues.Count > 0)
                {
                    progress?.Report(1);
                    if (choice.FellBack) notice = $"No captions in the chosen language; used {choice.Code}.";
                    return Save(cues, info, choice.IsManual ? TranscriptSource.OfficialCaptions : TranscriptSource.AutoCaptions, choice.Code, outputFolder, options, notice);
                }
            }

            // no usable captions: download the audio and transcribe it here
            if (_audio is null) throw new YouTubeException(YouTubeFailure.NoTranscriber, Message(YouTubeFailure.NoTranscriber, ""));
            if (!_hasJsRuntime()) throw new YouTubeException(YouTubeFailure.NeedsJavaScriptRuntime, Message(YouTubeFailure.NeedsJavaScriptRuntime, ""));
            var (code, output) = await _run($"-f \"bestaudio/best\" -x --audio-format wav --no-playlist --no-warnings -o \"{work}\\%(id)s.%(ext)s\" \"https://www.youtube.com/watch?v={id}\"");
            var audioFile = Directory.GetFiles(work, "*.wav").FirstOrDefault();
            if (code != 0 || audioFile is null) { var kind = Classify(output); throw new YouTubeException(kind, Message(kind, output)); }

            var imported = await _audio.ImportAsync(audioFile, outputFolder, options with { }, progress, ct);
            var title = SafeName(info.Title);
            return new YouTubeResult(imported.Cues, imported.TxtPath, imported.SrtPath, info, TranscriptSource.LocalTranscription, options.Language == Languages.Auto ? null : options.Language,
                "No captions were available; the audio was transcribed locally.");
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }

    private async Task<IReadOnlyList<SubtitleCue>> FetchCaptionsAsync(string id, CaptionChoice choice, string work)
    {
        var kind = choice.IsManual ? "--write-subs" : "--write-auto-subs";
        var (code, output) = await _run($"--skip-download {kind} --sub-langs \"{choice.Code}\" --sub-format vtt --no-playlist --no-warnings -o \"{work}\\%(id)s.%(ext)s\" \"https://www.youtube.com/watch?v={id}\"");
        var file = Directory.GetFiles(work, "*.vtt").FirstOrDefault();
        if (file is null)
        {
            if (code != 0 && Classify(output) is var k and not YouTubeFailure.Other) throw new YouTubeException(k, Message(k, output));
            return Array.Empty<SubtitleCue>();
        }
        return SubtitleFormatter.Parse(await File.ReadAllTextAsync(file));
    }

    private static YouTubeResult Save(IReadOnlyList<SubtitleCue> cues, YouTubeInfo info, TranscriptSource source, string language, string outputFolder, ImportOptions options, string? notice)
    {
        Directory.CreateDirectory(outputFolder);
        var (txt, srt) = MediaImporter.UniquePaths(outputFolder, SafeName(info.Title), options.WriteSrt);
        File.WriteAllText(txt, SubtitleFormatter.ToText(cues), new System.Text.UTF8Encoding(false));
        if (srt is not null) File.WriteAllText(srt, SubtitleFormatter.ToSrt(cues), new System.Text.UTF8Encoding(false));
        return new YouTubeResult(cues, txt, srt, info, source, language, notice);
    }

    public static string SafeName(string title)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(title.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "YouTube video" : clean.Length > 100 ? clean[..100].TrimEnd() : clean;
    }
}
