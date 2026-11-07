using System.Globalization;
using System.Text.Json.Nodes;
using MeetWise.Core;
using MeetWise.Core.Logging;
using MeetWise.Core.Models;
using MeetWise.Speech;

namespace MeetWise.Audio;

public sealed record AudioTrack(int StreamIndex, string? Language, string? Title);

public sealed record ImportOptions(string Language = Languages.Auto, bool TranslateToEnglish = false, bool NoiseFilter = false, bool WriteSrt = true,
    double ChunkSeconds = MediaImporter.DefaultChunkSeconds, IReadOnlyList<string>? Vocabulary = null);

public sealed record ImportResult(IReadOnlyList<SubtitleCue> Cues, string TxtPath, string? SrtPath, AudioTrack Track, bool TrackMatchedLanguage, double DurationSeconds);

/// <summary>
/// Subtitles from any audio or video file, the way the subtitle extractor does it: the audio track is chosen by language tag (with its aliases),
/// long media is cut into 20-minute chunks so memory stays flat, an optional speech filter cleans noisy audio, and the result is written as
/// .txt (always) and .srt (optional) with names that never overwrite an earlier result.
/// </summary>
public sealed class MediaImporter
{
    public const double DefaultChunkSeconds = 20 * 60;

    /// <summary>The same filter chain as the subtitle extractor: spectral noise reduction plus a speech band-pass.</summary>
    public const string NoiseFilter = "afftdn=nr=15:nf=-30,highpass=f=200,lowpass=f=3000";

    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly Func<float[], string, bool, IReadOnlyList<WhisperSegment>> _transcribe;

    /// <param name="transcribe">Transcribes 16 kHz audio given (samples, language, translateToEnglish). Production uses Whisper; tests can inject a fake.</param>
    public MediaImporter(string ffmpegExe, Func<float[], string, bool, IReadOnlyList<WhisperSegment>> transcribe)
    {
        _ffmpeg = ffmpegExe;
        _ffprobe = FFmpegPack.FindProbe(ffmpegExe) ?? throw new FileNotFoundException("ffprobe.exe was not found next to ffmpeg.exe.");
        _transcribe = transcribe;
    }

    public static MediaImporter WithWhisper(string ffmpegExe, WhisperTranscriber whisper, IReadOnlyList<string>? vocabulary = null)
        => new(ffmpegExe, (samples, language, translate) =>
            whisper.TranscribeAsync(samples, language, vocabulary, threads: Math.Max(2, Environment.ProcessorCount / 2), translateToEnglish: translate).GetAwaiter().GetResult());

    // ---- tracks ----

    public async Task<(IReadOnlyList<AudioTrack> Tracks, double Duration)> ProbeAsync(string file)
    {
        var (code, output) = await FFmpegPack.RunAsync(_ffprobe, $"-v error -print_format json -show_streams -show_format \"{file}\"", 60_000);
        if (code != 0) throw new InvalidOperationException("The file could not be read: " + output.Trim());
        var json = JsonNode.Parse(output[output.IndexOf('{')..])!;
        var tracks = json["streams"]!.AsArray()
            .Where(s => s!["codec_type"]?.ToString() == "audio")
            .Select(s => new AudioTrack(int.Parse(s!["index"]!.ToString()), s["tags"]?["language"]?.ToString(), s["tags"]?["title"]?.ToString()))
            .ToList();
        double.TryParse(json["format"]?["duration"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration);
        return (tracks, duration);
    }

    /// <summary>The track tagged with the chosen language, else the first audio track (and a flag telling which happened).</summary>
    public static (AudioTrack Track, bool Matched) ChooseTrack(IReadOnlyList<AudioTrack> tracks, string language)
    {
        if (tracks.Count == 0) throw new InvalidOperationException("The file has no audio.");
        if (language != Languages.Auto)
        {
            var tags = Languages.TrackTags(language);
            var match = tracks.FirstOrDefault(t => t.Language is not null && tags.Contains(t.Language));
            if (match is not null) return (match, true);
        }
        return (tracks[0], false);
    }

    // ---- import ----

    public async Task<ImportResult> ImportAsync(string file, string outputFolder, ImportOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var (tracks, duration) = await ProbeAsync(file);
        var (track, matched) = ChooseTrack(tracks, options.Language);
        if (duration <= 0) throw new InvalidOperationException("The length of the file is unknown.");

        var cues = new List<SubtitleCue>();
        var temp = Path.Combine(Path.GetTempPath(), "meetwise-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            int chunks = (int)Math.Ceiling(duration / options.ChunkSeconds);
            for (int i = 0; i < chunks; i++)
            {
                ct.ThrowIfCancellationRequested();
                double start = i * options.ChunkSeconds;
                double length = Math.Min(options.ChunkSeconds, duration - start);
                var wav = Path.Combine(temp, $"chunk{i}.wav");
                await ExtractAsync(file, track.StreamIndex, start, length, options.NoiseFilter, wav);

                var samples = AudioFile.ReadMono16k(wav);
                File.Delete(wav);
                foreach (var s in await Task.Run(() => _transcribe(samples, options.Language, options.TranslateToEnglish), ct))
                    if (!LiveSubtitler.IsNoiseText(s.Text))
                        cues.Add(new SubtitleCue(start + s.Start, start + Math.Max(s.End, s.Start + 0.3), s.Text, "", options.TranslateToEnglish ? "en" : s.Language));
                progress?.Report((i + 1) / (double)chunks);
            }
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } }

        Directory.CreateDirectory(outputFolder);
        var (txt, srt) = UniquePaths(outputFolder, Path.GetFileNameWithoutExtension(file), options.WriteSrt);
        await File.WriteAllTextAsync(txt, SubtitleFormatter.ToText(cues), new System.Text.UTF8Encoding(false), ct);
        if (srt is not null) await File.WriteAllTextAsync(srt, SubtitleFormatter.ToSrt(cues), new System.Text.UTF8Encoding(false), ct);
        AppLog.Info($"Imported {Path.GetFileName(file)}: {cues.Count} subtitles, track {track.StreamIndex} ({track.Language ?? "untagged"}){(matched ? "" : ", language tag not matched")}");
        return new ImportResult(cues, txt, srt, track, matched, duration);
    }

    private async Task ExtractAsync(string file, int streamIndex, double start, double length, bool noiseFilter, string wavPath)
    {
        var filter = noiseFilter ? $"-af \"{NoiseFilter}\" " : "";
        var args = $"-hide_banner -loglevel error -y -ss {start.ToString("0.###", CultureInfo.InvariantCulture)} -t {length.ToString("0.###", CultureInfo.InvariantCulture)} " +
                   $"-i \"{file}\" -map 0:{streamIndex} -vn {filter}-ac 1 -ar 16000 -c:a pcm_s16le \"{wavPath}\"";
        var (code, output) = await FFmpegPack.RunAsync(_ffmpeg, args, 20 * 60_000);
        if (code != 0 || !File.Exists(wavPath)) throw new InvalidOperationException("Could not extract the audio: " + output.Trim());
    }

    /// <summary>Never overwrites: a second result for the same name gets a timestamp and counter suffix, as in the subtitle extractor.</summary>
    public static (string Txt, string? Srt) UniquePaths(string folder, string stem, bool writeSrt, Func<DateTime>? now = null)
    {
        var stamp = (now?.Invoke() ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (int counter = 0; ; counter++)
        {
            var suffix = counter == 0 ? "" : $"_{stamp}_{counter}";
            var txt = Path.Combine(folder, stem + suffix + ".txt");
            var srt = writeSrt ? Path.Combine(folder, stem + suffix + ".srt") : null;
            if (!File.Exists(txt) && (srt is null || !File.Exists(srt))) return (txt, srt);
        }
    }
}
