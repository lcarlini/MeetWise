using MeetWise.Audio;
using MeetWise.Core;
using MeetWise.Core.Models;
using MeetWise.Speech;

namespace MeetWise.Tests;

public class ImportTests
{
    private static async Task<string> FfmpegAsync() => await FFmpegPack.EnsureAsync(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "mw-imp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Dominant frequency of a clip, from zero crossings. Enough to tell a 440 Hz track from an 880 Hz one.</summary>
    private static double Frequency(float[] samples16k)
    {
        int crossings = 0;
        for (int i = 1; i < samples16k.Length; i++) if (samples16k[i - 1] < 0 != samples16k[i] < 0) crossings++;
        return crossings / 2.0 / (samples16k.Length / 16000.0);
    }

    private static double Rms(float[] x) => Math.Sqrt(x.Length == 0 ? 0 : x.Sum(v => (double)v * v) / x.Length);

    private static async Task<string> TwoTrackMediaAsync(string ffmpeg, string dir, string name = "two.mkv", int seconds = 3, string lang0 = "eng", string lang1 = "por")
    {
        var file = Path.Combine(dir, name);
        var args = $"-hide_banner -loglevel error -y -f lavfi -i sine=frequency=440:duration={seconds} -f lavfi -i sine=frequency=880:duration={seconds} " +
                   $"-map 0:a -map 1:a -metadata:s:a:0 language={lang0} -metadata:s:a:1 language={lang1} \"{file}\"";
        var (code, output) = await FFmpegPack.RunAsync(ffmpeg, args);
        Assert.True(code == 0, output);
        return file;
    }

    // ---- languages ----

    [Fact]
    public void TwentyLanguages_WithNamesInThreeInterfaceLanguages()
    {
        Assert.Equal(20, Languages.All.Count);
        Assert.Equal(20, Languages.All.Select(l => l.Code).Distinct().Count());
        Assert.All(Languages.All, l => Assert.False(string.IsNullOrWhiteSpace(l.English) || string.IsNullOrWhiteSpace(l.Portuguese) || string.IsNullOrWhiteSpace(l.Spanish)));
        Assert.Equal("Portuguese", Languages.Name("pt", "en"));
        Assert.Equal("Português", Languages.Name("pt", "pt"));
        Assert.Equal("Alemán", Languages.Name("de", "es"));
        Assert.Equal("Detectar automaticamente", Languages.Name("auto", "pt"));
        Assert.Equal("xx", Languages.Name("xx", "en"));
        Assert.True(Languages.IsKnown("auto") && Languages.IsKnown("ko") && !Languages.IsKnown("xx"));
    }

    [Fact]
    public void TrackTags_IncludeIso3_AndTheBibliographicAliases()
    {
        Assert.Contains("chi", Languages.TrackTags("zh"));
        Assert.Contains("zho", Languages.TrackTags("zh"));
        Assert.Contains("fre", Languages.TrackTags("fr"));
        Assert.Contains("ger", Languages.TrackTags("de"));
        Assert.Contains("por", Languages.TrackTags("pt"));
        Assert.Contains("POR", Languages.TrackTags("pt")); // case-insensitive
        Assert.Equal(new[] { "pt", "pt-BR", "pt-PT" }, Languages.YouTubeCodes("pt"));
        Assert.Equal(new[] { "ko" }, Languages.YouTubeCodes("ko"));
    }

    [Fact]
    public void ChooseTrack_PicksTheTaggedLanguage_ElseTheFirstTrack()
    {
        var tracks = new[] { new AudioTrack(1, "eng", null), new AudioTrack(2, "por", "Dublado"), new AudioTrack(3, "chi", null) };
        Assert.Equal((2, true), (MediaImporter.ChooseTrack(tracks, "pt").Track.StreamIndex, true));
        Assert.True(MediaImporter.ChooseTrack(tracks, "pt").Matched);
        Assert.Equal(3, MediaImporter.ChooseTrack(tracks, "zh").Track.StreamIndex);   // chi is the alias of zh
        var fallback = MediaImporter.ChooseTrack(tracks, "ja");
        Assert.Equal((1, false), (fallback.Track.StreamIndex, fallback.Matched));      // not present: first track, flagged
        Assert.Equal(1, MediaImporter.ChooseTrack(tracks, "auto").Track.StreamIndex);
        Assert.Equal(1, MediaImporter.ChooseTrack(new[] { new AudioTrack(1, null, null) }, "pt").Track.StreamIndex); // untagged
        Assert.Throws<InvalidOperationException>(() => MediaImporter.ChooseTrack(Array.Empty<AudioTrack>(), "en"));
    }

    // ---- real media ----

    private static MediaImporter Spy(string ffmpeg, List<(double Freq, double Seconds)> seen, Func<int, IReadOnlyList<WhisperSegment>>? result = null)
    {
        int call = 0;
        return new MediaImporter(ffmpeg, (samples, language, translate) =>
        {
            lock (seen) seen.Add((Frequency(samples), samples.Length / 16000.0));
            return result?.Invoke(call++) ?? new[] { new WhisperSegment(0.1, 0.9, "hello from the test", language == "auto" ? "en" : language) };
        });
    }

    [Fact]
    public async Task Import_ProbesTracks_AndUsesTheTrackOfTheChosenLanguage()
    {
        var ffmpeg = await FfmpegAsync();
        var dir = NewDir();
        var media = await TwoTrackMediaAsync(ffmpeg, dir);
        var seen = new List<(double, double)>();
        var importer = Spy(ffmpeg, seen);

        var (tracks, duration) = await importer.ProbeAsync(media);
        Assert.Equal(new[] { "eng", "por" }, tracks.Select(t => t.Language));
        Assert.InRange(duration, 2.9, 3.2);

        var pt = await importer.ImportAsync(media, Path.Combine(dir, "out"), new ImportOptions(Language: "pt"));
        Assert.True(pt.TrackMatchedLanguage);
        Assert.Equal("por", pt.Track.Language);
        Assert.InRange(seen[0].Item1, 860, 900);          // the 880 Hz track was extracted

        seen.Clear();
        var en = await importer.ImportAsync(media, Path.Combine(dir, "out"), new ImportOptions(Language: "en"));
        Assert.InRange(seen[0].Item1, 420, 460);          // the 440 Hz track

        seen.Clear();
        var ja = await importer.ImportAsync(media, Path.Combine(dir, "out"), new ImportOptions(Language: "ja"));
        Assert.False(ja.TrackMatchedLanguage);            // no Japanese track: falls back to the first one and says so
        Assert.InRange(seen[0].Item1, 420, 460);
    }

    [Fact]
    public async Task Import_CutsLongMediaIntoChunks_AndKeepsTimestampsAbsolute()
    {
        var ffmpeg = await FfmpegAsync();
        var dir = NewDir();
        var media = await TwoTrackMediaAsync(ffmpeg, dir, seconds: 5);
        var seen = new List<(double, double)>();
        var importer = Spy(ffmpeg, seen); // each chunk "says" something at 0.1 s into the chunk

        var r = await importer.ImportAsync(media, Path.Combine(dir, "out"), new ImportOptions(Language: "auto", ChunkSeconds: 2));
        Assert.Equal(3, seen.Count);                                            // 2 s + 2 s + 1 s
        Assert.InRange(seen[0].Item2, 1.9, 2.1);
        Assert.InRange(seen[2].Item2, 0.9, 1.1);
        Assert.Equal(new[] { 0.1, 2.1, 4.1 }, r.Cues.Select(c => Math.Round(c.Start, 1)));
        Assert.All(seen, s => Assert.InRange(s.Item1, 420, 460));              // auto: the first track in every chunk
    }

    [Fact]
    public async Task NoiseFilter_CutsHissOutsideTheSpeechBand_ButKeepsSpeechFrequencies()
    {
        var ffmpeg = await FfmpegAsync();
        var dir = NewDir();
        async Task<(double Without, double With)> Levels(int hz)
        {
            var media = Path.Combine(dir, $"tone{hz}.mkv");
            var (code, o) = await FFmpegPack.RunAsync(ffmpeg, $"-hide_banner -loglevel error -y -f lavfi -i sine=frequency={hz}:duration=2 \"{media}\"");
            Assert.True(code == 0, o);
            var levels = new List<double>();
            var importer = new MediaImporter(ffmpeg, (samples, _, _) => { levels.Add(Rms(samples)); return Array.Empty<WhisperSegment>(); });
            await importer.ImportAsync(media, Path.Combine(dir, "out"), new ImportOptions(NoiseFilter: false));
            await importer.ImportAsync(media, Path.Combine(dir, "out"), new ImportOptions(NoiseFilter: true));
            return (levels[0], levels[1]);
        }

        var hiss = await Levels(6000);
        Assert.True(hiss.With < hiss.Without * 0.3, $"6 kHz hiss must be reduced: {hiss.Without:0.000} -> {hiss.With:0.000}");
        var speechBand = await Levels(1000);
        Assert.True(speechBand.With > speechBand.Without * 0.6, $"1 kHz (speech) must pass: {speechBand.Without:0.000} -> {speechBand.With:0.000}");
    }

    [Fact]
    public async Task Import_WritesTxtAndOptionalSrt_AndNeverOverwritesAnEarlierResult()
    {
        var ffmpeg = await FfmpegAsync();
        var dir = NewDir();
        var media = await TwoTrackMediaAsync(ffmpeg, dir, name: "Weekly sync.mkv", seconds: 2);
        var importer = Spy(ffmpeg, new List<(double, double)>());
        var outDir = Path.Combine(dir, "out");

        var first = await importer.ImportAsync(media, outDir, new ImportOptions(WriteSrt: true));
        Assert.Equal(Path.Combine(outDir, "Weekly sync.txt"), first.TxtPath);
        Assert.Equal("hello from the test\n", File.ReadAllText(first.TxtPath));
        Assert.StartsWith("1\n00:00:00,100 --> 00:00:00,900\nhello from the test", File.ReadAllText(first.SrtPath!));

        var second = await importer.ImportAsync(media, outDir, new ImportOptions(WriteSrt: false));
        Assert.NotEqual(first.TxtPath, second.TxtPath);
        Assert.Matches(@"Weekly sync_\d{8}-\d{6}_1\.txt$", second.TxtPath);
        Assert.Null(second.SrtPath);
        Assert.True(File.Exists(first.TxtPath));          // the first result is untouched
    }

    [Fact]
    public async Task Import_DropsWhisperNoise_AndReportsProgress()
    {
        var ffmpeg = await FfmpegAsync();
        var dir = NewDir();
        var media = await TwoTrackMediaAsync(ffmpeg, dir, seconds: 4);
        var importer = Spy(ffmpeg, new List<(double, double)>(), call => new[]
        {
            new WhisperSegment(0, 1, "[BLANK_AUDIO]", "en"), new WhisperSegment(1, 2, "A real sentence.", "en"), new WhisperSegment(2, 3, "...", "en")
        });
        var steps = new List<double>();
        var r = await importer.ImportAsync(media, Path.Combine(dir, "out"), new ImportOptions(ChunkSeconds: 2), new Progress<double>(steps.Add));
        await Task.Delay(100);
        Assert.All(r.Cues, c => Assert.Equal("A real sentence.", c.Text));
        Assert.Equal(steps.Count, r.Cues.Count);   // one real sentence per chunk; the markers and the dots are dropped
        Assert.True(steps.Count >= 2);
        Assert.Equal(1.0, steps.Max());
    }

    [Fact]
    public async Task Import_ReportsAClearError_ForFilesWithoutAudio_OrNotMedia()
    {
        var ffmpeg = await FfmpegAsync();
        var dir = NewDir();
        var silentVideo = Path.Combine(dir, "mute.mkv");
        var (c, o) = await FFmpegPack.RunAsync(ffmpeg, $"-hide_banner -loglevel error -y -f lavfi -i testsrc=s=160x90:r=10 -t 1 -c:v libx264 \"{silentVideo}\"");
        Assert.True(c == 0, o);
        var importer = Spy(ffmpeg, new List<(double, double)>());
        var noAudio = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(silentVideo, dir, new ImportOptions()));
        Assert.Contains("no audio", noAudio.Message);

        var junk = Path.Combine(dir, "notes.mp4");
        File.WriteAllText(junk, "this is not a video");
        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(junk, dir, new ImportOptions()));
    }

    [Fact]
    public async Task Import_WithRealWhisper_TranscribesSpeechFromAVideoFile()
    {
        var model = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models", "whisper", "ggml-base.bin");
        var wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models", "asr",
            "sherpa-onnx-streaming-zipformer-en-2023-06-26", "test_wavs", "1.wav");
        if (!File.Exists(model) || !File.Exists(wav)) return;
        MeetWise.Core.Models.CudaRuntimePack.Activate();
        var ffmpeg = await FfmpegAsync();
        var dir = NewDir();
        var video = Path.Combine(dir, "lesson.mp4"); // speech inside a real video container (picture + AAC audio)
        var (c, o) = await FFmpegPack.RunAsync(ffmpeg, $"-hide_banner -loglevel error -y -f lavfi -i testsrc=s=320x180:r=10 -i \"{wav}\" -shortest -c:v libx264 -c:a aac \"{video}\"");
        Assert.True(c == 0, o);

        using var whisper = new WhisperTranscriber(model);
        var r = await MediaImporter.WithWhisper(ffmpeg, whisper).ImportAsync(video, Path.Combine(dir, "out"), new ImportOptions(Language: "en"));
        var text = string.Join(" ", r.Cues.Select(x => x.Text)).ToUpperInvariant();
        Assert.Contains("DIRECT CONSEQUENCE", text);
        Assert.Contains("DESCENT OF MORTALS", text);
        Assert.True(File.Exists(r.SrtPath!));
        Assert.True(r.Cues.Zip(r.Cues.Skip(1)).All(p => p.First.Start <= p.Second.Start));
    }
}
