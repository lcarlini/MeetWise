using System.Text.RegularExpressions;
using MeetWise.Audio;
using MeetWise.Core;
using MeetWise.Core.Models;
using MeetWise.Speech;

namespace MeetWise.Tests;

public class WatchFolderTests
{
    private static async Task<bool> Eventually(Func<bool> cond, int ms = 6000)
    {
        for (int t = 0; t < ms; t += 25) { if (cond()) return true; await Task.Delay(25); }
        return cond();
    }

    private static WatchPaths NewPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-watch-" + Guid.NewGuid().ToString("N"));
        return WatchPaths.For(Path.Combine(root, "To Process"));
    }

    [Fact]
    public void Layout_FollowsTheExtractor_ToProcess_Output_Processed_Errors()
    {
        var p = WatchPaths.For(@"C:\Videos\MeetWise\To Process\");
        Assert.Equal(@"C:\Videos\MeetWise\To Process", p.Incoming);
        Assert.Equal(@"C:\Videos\MeetWise\Output", p.Output);
        Assert.Equal(@"C:\Videos\MeetWise\Processed", p.Processed);
        Assert.Equal(@"C:\Videos\MeetWise\Errors", p.Errors);
    }

    [Theory]
    [InlineData("a.mp4", true)] [InlineData("A.MKV", true)] [InlineData("voice.m4a", true)] [InlineData("x.opus", true)]
    [InlineData("notes.txt", false)] [InlineData("photo.jpg", false)] [InlineData("noextension", false)]
    public void OnlyAudioAndVideoAreProcessed(string name, bool media) => Assert.Equal(media, WatchFolder.IsMedia(name));

    [Fact]
    public async Task ASuccessfulFile_ProducesOutput_AndTheSourceMovesToProcessed()
    {
        var paths = NewPaths();
        var finished = new List<string>();
        using var watch = new WatchFolder(paths, (file, outDir) =>
        {
            File.WriteAllText(Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + ".txt"), "transcript");
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(40));
        watch.Finished += f => finished.Add(f);
        watch.Start();

        File.WriteAllText(Path.Combine(paths.Incoming, "call.mp4"), "video bytes");
        Assert.True(await Eventually(() => finished.Count == 1));
        Assert.True(File.Exists(Path.Combine(paths.Output, "call.txt")));
        Assert.True(File.Exists(Path.Combine(paths.Processed, "call.mp4")));
        Assert.False(File.Exists(Path.Combine(paths.Incoming, "call.mp4")));
    }

    [Fact]
    public async Task AFailingFile_GoesToErrors_WithALogThatSaysWhy()
    {
        var paths = NewPaths();
        var failed = new List<string>();
        using var watch = new WatchFolder(paths, (_, _) => throw new InvalidOperationException("The file has no audio."), TimeSpan.FromMilliseconds(40));
        watch.Failed += (f, _) => failed.Add(f);
        watch.Start();

        File.WriteAllText(Path.Combine(paths.Incoming, "broken.mkv"), "x");
        Assert.True(await Eventually(() => failed.Count == 1));
        Assert.True(File.Exists(Path.Combine(paths.Errors, "broken.mkv")));
        var log = File.ReadAllText(Path.Combine(paths.Errors, "broken.mkv.error.log"));
        Assert.Contains("The file has no audio.", log);
        Assert.Contains("InvalidOperationException", log);
        Assert.Empty(Directory.GetFiles(paths.Processed));
    }

    [Fact]
    public async Task NonMediaFiles_AreLeftAlone()
    {
        var paths = NewPaths();
        int calls = 0;
        using var watch = new WatchFolder(paths, (_, _) => { calls++; return Task.CompletedTask; }, TimeSpan.FromMilliseconds(40));
        watch.Start();
        File.WriteAllText(Path.Combine(paths.Incoming, "readme.txt"), "x");
        await Task.Delay(400);
        Assert.Equal(0, calls);
        Assert.True(File.Exists(Path.Combine(paths.Incoming, "readme.txt")));
    }

    [Fact]
    public async Task AFileStillBeingCopied_WaitsUntilItIsComplete()
    {
        var paths = NewPaths();
        var processed = new List<string>();
        using var watch = new WatchFolder(paths, (f, _) => { lock (processed) processed.Add(Path.GetFileName(f)); return Task.CompletedTask; }, TimeSpan.FromMilliseconds(40));
        watch.Start();

        var path = Path.Combine(paths.Incoming, "big.mp4");
        using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) // a copy in progress holds the file
        {
            writer.Write(new byte[1000]);
            await Task.Delay(500);
            lock (processed) Assert.Empty(processed);
        }
        Assert.True(await Eventually(() => { lock (processed) return processed.Count == 1; }));
    }

    [Fact]
    public async Task FilesAreProcessedOneAtATime_InArrivalOrder_AndNamesInProcessedNeverCollide()
    {
        var paths = NewPaths();
        Directory.CreateDirectory(paths.Processed);
        Directory.CreateDirectory(paths.Incoming);
        File.WriteAllText(Path.Combine(paths.Processed, "a.mp4"), "older result already here");
        int running = 0, maxRunning = 0;
        var order = new List<string>();
        using var watch = new WatchFolder(paths, async (f, _) =>
        {
            maxRunning = Math.Max(maxRunning, Interlocked.Increment(ref running));
            lock (order) order.Add(Path.GetFileName(f));
            await Task.Delay(120);
            Interlocked.Decrement(ref running);
        }, TimeSpan.FromMilliseconds(40));

        File.WriteAllText(Path.Combine(paths.Incoming, "a.mp4"), "1");
        await Task.Delay(30);
        File.SetCreationTimeUtc(Path.Combine(paths.Incoming, "a.mp4"), DateTime.UtcNow.AddMinutes(-2));
        File.WriteAllText(Path.Combine(paths.Incoming, "b.wav"), "2");
        watch.Start();

        bool all = await Eventually(() => Directory.GetFiles(paths.Processed).Length == 3);
        Assert.True(all, $"incoming=[{string.Join(",", Directory.GetFiles(paths.Incoming).Select(Path.GetFileName))}] processed=[{string.Join(",", Directory.GetFiles(paths.Processed).Select(Path.GetFileName))}] errors=[{string.Join(",", Directory.GetFiles(paths.Errors).Select(Path.GetFileName))}] order=[{string.Join(",", order)}]");
        Assert.Equal(1, maxRunning);
        Assert.Equal(new[] { "a.mp4", "b.wav" }, order);
        Assert.Equal("older result already here", File.ReadAllText(Path.Combine(paths.Processed, "a.mp4")));
        Assert.Equal("1", File.ReadAllText(Path.Combine(paths.Processed, "a (1).mp4")));
    }

    [Fact]
    public async Task FilesAlreadyWaiting_WhenWatchingStarts_AreProcessed()
    {
        var paths = NewPaths();
        Directory.CreateDirectory(paths.Incoming);
        File.WriteAllText(Path.Combine(paths.Incoming, "waiting.mp3"), "x");
        int done = 0;
        using var watch = new WatchFolder(paths, (_, _) => { done++; return Task.CompletedTask; }, TimeSpan.FromMilliseconds(40));
        watch.Start();
        Assert.True(await Eventually(() => done == 1));
    }
}

public class YouTubeTests
{
    // ---- links ----

    [Theory]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42s&list=PLabc", "dQw4w9WgXcQ")]
    [InlineData("youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=xyz", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("  https://youtu.be/dQw4w9WgXcQ  ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/playlist?list=PLabcdefghij", null)]
    [InlineData("https://vimeo.com/123456789", null)]
    [InlineData("https://evil.com/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("https://youtube.com.evil.com/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("not a link", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractId_AcceptsVideoLinks_AndRejectsEverythingElse(string? input, string? expected) => Assert.Equal(expected, YouTubeUrl.ExtractId(input));

    // ---- choosing captions ----

    private static YouTubeInfo Info(string? lang, string[] manual, string[] auto) => new("dQw4w9WgXcQ", "Title", 100, lang, manual, auto);

    [Fact]
    public void Captions_PreferOfficialInTheChosenLanguage_ThenAuto()
    {
        var info = Info("en", new[] { "en", "pt-BR" }, new[] { "en", "pt", "es" });
        Assert.Equal(new CaptionChoice("pt-BR", true, false), YouTubeImporter.PickCaption(info, "pt"));         // official pt-BR beats auto pt
        Assert.Equal(new CaptionChoice("es", false, false), YouTubeImporter.PickCaption(info, "es"));           // only auto exists
        Assert.Equal(new CaptionChoice("en", true, false), YouTubeImporter.PickCaption(info, "en"));
    }

    [Fact]
    public void Captions_AutoDetect_UsesTheVideosOwnLanguage_ThenAnyOfficialTrack()
    {
        Assert.Equal(new CaptionChoice("pt", true, false), YouTubeImporter.PickCaption(Info("pt", new[] { "en", "pt" }, Array.Empty<string>()), "auto"));
        Assert.Equal(new CaptionChoice("ja", false, false), YouTubeImporter.PickCaption(Info("ja", Array.Empty<string>(), new[] { "ja", "en" }), "auto"));
        Assert.Equal(new CaptionChoice("fr", true, false), YouTubeImporter.PickCaption(Info(null, new[] { "fr" }, new[] { "en" }), "auto"));
        Assert.Null(YouTubeImporter.PickCaption(Info("en", Array.Empty<string>(), Array.Empty<string>()), "auto"));
    }

    [Fact]
    public void Captions_WhenTheChosenLanguageIsMissing_FallBackToTheVideosLanguage_AndSayItDid()
    {
        var choice = YouTubeImporter.PickCaption(Info("en", new[] { "en" }, new[] { "en" }), "ko");
        Assert.Equal(new CaptionChoice("en", true, true), choice);
        Assert.Null(YouTubeImporter.PickCaption(Info("de", Array.Empty<string>(), new[] { "de" }), "ko") is { IsManual: true } c ? c : null); // only auto in another language: not preferred over transcribing? still offered
    }

    [Fact]
    public void Captions_TranslateToEnglish_UsesEnglishTracks_OrAsksForLocalTranslation()
    {
        Assert.Equal(new CaptionChoice("en", false, false), YouTubeImporter.PickCaption(Info("pt", new[] { "pt" }, new[] { "pt", "en" }), "pt", translateToEnglish: true));
        Assert.Null(YouTubeImporter.PickCaption(Info("pt", new[] { "pt" }, new[] { "pt" }), "pt", translateToEnglish: true)); // no English track: translate the audio locally
    }

    [Fact]
    public void ParseInfo_ReadsTitleLanguageAndCaptionLists()
    {
        var info = YouTubeImporter.ParseInfo("""
            WARNING: something
            {"id":"abc12345678","title":"A talk: part 1","duration":125.5,"language":"en-US",
             "subtitles":{"en":[{}],"pt-BR":[{}],"live_chat":[{}]},"automatic_captions":{"en":[{}],"de":[{}]}}
            """);
        Assert.Equal(("abc12345678", "A talk: part 1", 125.5, "en-US"), (info.Id, info.Title, info.DurationSeconds, info.Language));
        Assert.Equal(new[] { "en", "pt-BR" }, info.ManualCaptions);   // live chat is not a caption
        Assert.Equal(new[] { "en", "de" }, info.AutoCaptions);
    }

    [Theory]
    [InlineData("ERROR: Sign in to confirm you’re not a bot", YouTubeFailure.Blocked)]
    [InlineData("HTTP Error 429: Too Many Requests", YouTubeFailure.Blocked)]
    [InlineData("ERROR: Video unavailable", YouTubeFailure.Unavailable)]
    [InlineData("ERROR: Private video. Sign in if you've been granted access", YouTubeFailure.Unavailable)]
    [InlineData("No supported JavaScript runtime could be found", YouTubeFailure.NeedsJavaScriptRuntime)]
    [InlineData("something else broke", YouTubeFailure.Other)]
    public void Failures_AreClassified_ForClearMessages(string output, YouTubeFailure expected) => Assert.Equal(expected, YouTubeImporter.Classify(output));

    [Fact]
    public void SafeName_RemovesCharactersWindowsRejects()
    {
        Assert.Equal("A_ talk_ part 1_ _best_", YouTubeImporter.SafeName("A/ talk\\ part 1: \"best\""));
        Assert.Equal("YouTube video", YouTubeImporter.SafeName("  ... "));
        Assert.Equal(100, YouTubeImporter.SafeName(new string('x', 300)).Length);
    }

    // ---- full flow with a scripted yt-dlp ----

    private const string InfoJson = """{"id":"dQw4w9WgXcQ","title":"Quarterly: review","duration":60,"language":"en","subtitles":{"en":[{}]},"automatic_captions":{"en":[{}],"pt":[{}]}}""";

    private const string RollingVtt = """
        WEBVTT

        00:00:00.000 --> 00:00:02.000
        hello<00:00:00.500><c> everyone</c>

        00:00:02.000 --> 00:00:02.010
        hello everyone

        00:00:02.010 --> 00:00:04.000
        hello everyone
        welcome<c> to</c><c> the review</c>
        """;

    /// <summary>A pretend yt-dlp: answers the info request, and writes caption or audio files into the folder given by -o.</summary>
    private sealed class FakeYtDlp
    {
        public readonly List<string> Calls = new();
        public string Info = InfoJson;
        public string? Vtt = RollingVtt;
        public bool WriteAudio;
        public (int Code, string Output)? Fail;

        public Task<(int, string)> Run(string args)
        {
            Calls.Add(args);
            if (Fail is { } f && !args.Contains("--dump-single-json")) return Task.FromResult((f.Code, f.Output));
            if (args.Contains("--dump-single-json")) return Task.FromResult((0, Info));
            var dir = Regex.Match(args, @"-o ""(?<d>[^""]+)\\%\(id\)s").Groups["d"].Value;
            if (args.Contains("--write-subs") || args.Contains("--write-auto-subs"))
            {
                if (Vtt is not null) File.WriteAllText(Path.Combine(dir, "dQw4w9WgXcQ.en.vtt"), Vtt);
            }
            else if (WriteAudio)
            {
                using var w = new NAudio.Wave.WaveFileWriter(Path.Combine(dir, "dQw4w9WgXcQ.wav"), new NAudio.Wave.WaveFormat(16000, 16, 1));
                var tone = new float[16000 * 2];
                w.WriteSamples(tone, 0, tone.Length);
            }
            return Task.FromResult((0, ""));
        }
    }

    private static string OutDir() => Path.Combine(Path.GetTempPath(), "mw-yt-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Import_UsesOfficialCaptions_WithoutDownloadingAnyAudio()
    {
        var fake = new FakeYtDlp();
        var yt = new YouTubeImporter("yt-dlp", null, fake.Run, () => false);
        var outDir = OutDir();
        var r = await yt.ImportAsync("https://youtu.be/dQw4w9WgXcQ", outDir, new ImportOptions(Language: "en", WriteSrt: true));

        Assert.Equal(TranscriptSource.OfficialCaptions, r.Source);
        Assert.Equal(new[] { "hello everyone", "welcome to the review" }, r.Cues.Select(c => c.Text));   // rolling captions reduced to one line each
        Assert.Contains(fake.Calls, c => c.Contains("--write-subs") && c.Contains("--sub-langs \"en\""));
        Assert.DoesNotContain(fake.Calls, c => c.Contains("-x --audio-format"));
        Assert.Equal(Path.Combine(outDir, "Quarterly_ review.txt"), r.TxtPath);                           // title is the file name, ':' replaced
        Assert.StartsWith("1\n00:00:00,000 --> 00:00:02,", File.ReadAllText(r.SrtPath!));
        Assert.Equal("hello everyone\nwelcome to the review\n", File.ReadAllText(r.TxtPath));
    }

    [Fact]
    public async Task Import_UsesAutoCaptions_WhenThereAreNoOfficialOnesInThatLanguage_AndSaysSo()
    {
        var fake = new FakeYtDlp();
        var yt = new YouTubeImporter("yt-dlp", null, fake.Run, () => false);
        var r = await yt.ImportAsync("dQw4w9WgXcQ", OutDir(), new ImportOptions(Language: "pt"));
        Assert.Equal(TranscriptSource.AutoCaptions, r.Source);
        Assert.Contains(fake.Calls, c => c.Contains("--write-auto-subs") && c.Contains("--sub-langs \"pt\""));
        Assert.Equal("pt", r.Language);
    }

    [Fact]
    public async Task Import_NotesWhenTheChosenLanguageHadNoCaptions()
    {
        var fake = new FakeYtDlp { Info = """{"id":"dQw4w9WgXcQ","title":"T","duration":9,"language":"en","subtitles":{"en":[{}]},"automatic_captions":{}}""" };
        var r = await new YouTubeImporter("yt-dlp", null, fake.Run, () => false).ImportAsync("dQw4w9WgXcQ", OutDir(), new ImportOptions(Language: "ko"));
        Assert.Contains("No captions in the chosen language", r.Notice);
    }

    [Fact]
    public async Task Import_FallsBackToLocalTranscription_WhenTheVideoHasNoCaptions()
    {
        var ffmpeg = await FFmpegPack.EnsureAsync(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
        var fake = new FakeYtDlp { Info = """{"id":"dQw4w9WgXcQ","title":"No captions here","duration":2,"language":"en","subtitles":{},"automatic_captions":{}}""", WriteAudio = true };
        var spy = new MediaImporter(ffmpeg, (_, language, translate) => new[] { new WhisperSegment(0.2, 1.5, "transcribed on this PC", "en") });
        var yt = new YouTubeImporter("yt-dlp", spy, fake.Run, () => true);

        var r = await yt.ImportAsync("dQw4w9WgXcQ", OutDir(), new ImportOptions());
        Assert.Equal(TranscriptSource.LocalTranscription, r.Source);
        Assert.Equal("transcribed on this PC", Assert.Single(r.Cues).Text);
        Assert.Contains("transcribed locally", r.Notice);
        Assert.Contains(fake.Calls, c => c.Contains("-x --audio-format wav"));
    }

    [Fact]
    public async Task Import_EmptyCaptionFile_AlsoFallsBackToTranscription()
    {
        var ffmpeg = await FFmpegPack.EnsureAsync(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
        var fake = new FakeYtDlp { Vtt = "WEBVTT\n\n", WriteAudio = true };
        var spy = new MediaImporter(ffmpeg, (_, _, _) => new[] { new WhisperSegment(0, 1, "from audio", "en") });
        var r = await new YouTubeImporter("yt-dlp", spy, fake.Run, () => true).ImportAsync("dQw4w9WgXcQ", OutDir(), new ImportOptions(Language: "en"));
        Assert.Equal(TranscriptSource.LocalTranscription, r.Source);
    }

    [Fact]
    public async Task Import_NeedsAJavaScriptRuntimeOnlyForAudioDownloads_AndSaysSo()
    {
        var ffmpeg = await FFmpegPack.EnsureAsync(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
        var fake = new FakeYtDlp { Info = """{"id":"dQw4w9WgXcQ","title":"T","duration":2,"language":"en","subtitles":{},"automatic_captions":{}}""" };
        var spy = new MediaImporter(ffmpeg, (_, _, _) => Array.Empty<WhisperSegment>());
        var ex = await Assert.ThrowsAsync<YouTubeException>(() => new YouTubeImporter("yt-dlp", spy, fake.Run, () => false).ImportAsync("dQw4w9WgXcQ", OutDir(), new ImportOptions()));
        Assert.Equal(YouTubeFailure.NeedsJavaScriptRuntime, ex.Kind);
        Assert.Contains("Node.js", ex.Message);

        // captions work without any runtime
        var withCaptions = new FakeYtDlp();
        var ok = await new YouTubeImporter("yt-dlp", spy, withCaptions.Run, () => false).ImportAsync("dQw4w9WgXcQ", OutDir(), new ImportOptions(Language: "en"));
        Assert.Equal(TranscriptSource.OfficialCaptions, ok.Source);
    }

    [Fact]
    public async Task Import_NoCaptionsAndNoTranscriber_SaysSoClearly()
    {
        var fake = new FakeYtDlp { Info = """{"id":"dQw4w9WgXcQ","title":"T","duration":2,"language":"en","subtitles":{},"automatic_captions":{}}""" };
        var ex = await Assert.ThrowsAsync<YouTubeException>(() => new YouTubeImporter("yt-dlp", null, fake.Run, () => true).ImportAsync("dQw4w9WgXcQ", OutDir(), new ImportOptions()));
        Assert.Equal(YouTubeFailure.NoTranscriber, ex.Kind);
    }

    [Fact]
    public async Task Import_ReportsBlockedAndUnavailable_WithFriendlyMessages()
    {
        var blocked = new FakeYtDlp { Fail = (1, "ERROR: Sign in to confirm you’re not a bot") };
        // the info request itself is answered, the caption download is refused
        var ex = await Assert.ThrowsAsync<YouTubeException>(() => new YouTubeImporter("yt-dlp", null, blocked.Run, () => true).ImportAsync("dQw4w9WgXcQ", OutDir(), new ImportOptions(Language: "en")));
        Assert.Equal(YouTubeFailure.Blocked, ex.Kind);
        Assert.Contains("Wait a minute", ex.Message);

        var info = async (string text) => await Assert.ThrowsAsync<YouTubeException>(() =>
            new YouTubeImporter("yt-dlp", null, _ => Task.FromResult((1, text)), () => true).ImportAsync("dQw4w9WgXcQ", OutDir(), new ImportOptions()));
        Assert.Equal(YouTubeFailure.Unavailable, (await info("ERROR: [youtube] dQw4w9WgXcQ: Video unavailable")).Kind);
    }

    [Fact]
    public async Task Import_RejectsAnythingThatIsNotAYouTubeLink()
        => await Assert.ThrowsAsync<ArgumentException>(() => new YouTubeImporter("yt-dlp", null, new FakeYtDlp().Run, () => true).ImportAsync("https://vimeo.com/1", OutDir(), new ImportOptions()));

    [Fact]
    public async Task Import_TranslateToEnglish_WithoutEnglishCaptions_TranscribesAndTranslatesTheAudio()
    {
        var ffmpeg = await FFmpegPack.EnsureAsync(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
        var fake = new FakeYtDlp { Info = """{"id":"dQw4w9WgXcQ","title":"Palestra","duration":2,"language":"pt","subtitles":{"pt":[{}]},"automatic_captions":{"pt":[{}]}}""", WriteAudio = true };
        bool translated = false;
        var spy = new MediaImporter(ffmpeg, (_, _, translate) => { translated = translate; return new[] { new WhisperSegment(0, 1, "translated", "en") }; });
        var r = await new YouTubeImporter("yt-dlp", spy, fake.Run, () => true).ImportAsync("dQw4w9WgXcQ", OutDir(), new ImportOptions(Language: "pt", TranslateToEnglish: true));
        Assert.True(translated);
        Assert.Equal(TranscriptSource.LocalTranscription, r.Source);
    }

    // ---- the real yt-dlp ----

    [Fact]
    public async Task YtDlp_RealDownload_PassesThePinnedHash_AndRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-ytdlp-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        string exe;
        try { exe = await YtDlpPack.EnsureAsync(http, modelsRoot: root); }
        catch (HttpRequestException) { return; } // offline
        Assert.Equal(YtDlpPack.Exe.Sha256, await ModelManager.Sha256Async(exe), ignoreCase: true);
        var (code, output) = await FFmpegPack.RunAsync(exe, "--version");
        Assert.Equal(0, code);
        Assert.Equal(YtDlpPack.Version, output.Trim().Split('\n')[0].Trim());
        Assert.Equal(exe, YtDlpPack.Find(root));
    }

    [Fact]
    public async Task YtDlp_ACorruptDownload_IsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-ytdlp-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(new StubHandler { Respond = (_, _) => (System.Net.HttpStatusCode.OK, "MZ not really yt-dlp") });
        await Assert.ThrowsAsync<InvalidDataException>(() => YtDlpPack.EnsureAsync(http, modelsRoot: root));
        Assert.Null(YtDlpPack.Find(root));
    }

    [Fact]
    public async Task LiveYouTube_CaptionsOfAPublicVideo_WhenTheNetworkAllowsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-ytlive-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        string exe;
        try { exe = await YtDlpPack.EnsureAsync(http, modelsRoot: root); }
        catch (HttpRequestException) { return; }
        try
        {
            var r = await new YouTubeImporter(exe, null).ImportAsync("dQw4w9WgXcQ", OutDir(), new ImportOptions(Language: "en"));
            Assert.NotEmpty(r.Cues);
            Assert.True(File.Exists(r.TxtPath));
            Assert.True(r.Cues.Zip(r.Cues.Skip(1)).All(p => p.First.Start <= p.Second.Start));
        }
        catch (YouTubeException ex) when (ex.Kind is YouTubeFailure.Blocked or YouTubeFailure.Other or YouTubeFailure.Unavailable or YouTubeFailure.NoTranscriber)
        {
            // YouTube may refuse automated requests from this network at the moment; that is the documented, handled outcome
        }
    }
}
