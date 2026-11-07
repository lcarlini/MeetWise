using MeetWise.Audio;
using MeetWise.Core.Models;
using MeetWise.Speech;
using MeetWise.Storage;

namespace MeetWise.Tests;

public class ImportServiceTests
{
    private static async Task<string> FfmpegAsync() => await FFmpegPack.EnsureAsync(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "mw-is-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static MeetingDatabase NewDb(string dir) => new(Path.Combine(dir, "db", "t.db"));

    private static async Task<string> MakeMediaAsync(string ffmpeg, string path, bool video)
    {
        var args = video
            ? $"-hide_banner -loglevel error -y -f lavfi -i testsrc=s=160x90:r=10 -f lavfi -i sine=frequency=440:duration=2 -t 2 -c:v libx264 -c:a aac \"{path}\""
            : $"-hide_banner -loglevel error -y -f lavfi -i sine=frequency=440:duration=2 \"{path}\"";
        var (code, output) = await FFmpegPack.RunAsync(ffmpeg, args);
        Assert.True(code == 0, output);
        return path;
    }

    private static Func<Task<MediaImporter>> Fake(string ffmpeg, Func<WhisperSegment[]>? segments = null)
        => () => Task.FromResult(new MediaImporter(ffmpeg, (_, language, translate) => segments?.Invoke() ?? new[] { new WhisperSegment(0.2, 1.4, "imported words", "en") }));

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 8000)
    {
        for (int t = 0; t < ms; t += 25) { if (cond()) return true; await Task.Delay(25); }
        return cond();
    }

    [Fact]
    public async Task AFile_BecomesALibraryItem_WithSubtitles_AndItsVideoPath()
    {
        var ffmpeg = await FfmpegAsync();
        var dir = NewDir();
        var db = NewDb(dir);
        var video = await MakeMediaAsync(ffmpeg, Path.Combine(dir, "Lesson 1.mp4"), video: true);
        var audio = await MakeMediaAsync(ffmpeg, Path.Combine(dir, "Podcast.wav"), video: false);
        using var svc = new ImportService(db, Fake(ffmpeg));
        var seen = new List<JobStatus>();
        var added = new List<long>();
        svc.JobChanged += j => { lock (seen) seen.Add(j.Status); };
        svc.ItemAdded += added.Add;

        var jobV = svc.EnqueueFile(video, new ImportOptions(), Path.Combine(dir, "out"));
        var jobA = svc.EnqueueFile(audio, new ImportOptions(WriteSrt: false), Path.Combine(dir, "out"));
        await jobV.Completion.Task;
        await jobA.Completion.Task;

        Assert.Equal(JobStatus.Done, jobV.Status);
        Assert.Equal(1.0, jobV.Progress);
        lock (seen) Assert.Contains(JobStatus.Working, seen);

        var v = db.GetMeeting(jobV.ItemId!.Value)!;
        Assert.Equal(("import", "Lesson 1", video, null), (v.Kind, v.Title, v.VideoPath, v.AudioPath));
        Assert.Equal(video, v.Source);
        Assert.Equal("imported words", Assert.Single(db.GetTranscript(v.Id)).Text);
        Assert.Single(db.Search("imported"), s => s.MeetingId == v.Id);

        var a = db.GetMeeting(jobA.ItemId!.Value)!;
        Assert.Equal((audio, null), (a.AudioPath, a.VideoPath));          // audio files have no video
        Assert.True(File.Exists(Path.Combine(dir, "out", "Lesson 1.srt")));
        Assert.False(File.Exists(Path.Combine(dir, "out", "Podcast.srt"))); // .srt was switched off for this one
        Assert.Equal(new[] { v.Id, a.Id }, added.OrderBy(x => x));
    }

    [Fact]
    public async Task AFailedImport_KeepsItsReason_AndCreatesNoItem()
    {
        var ffmpeg = await FfmpegAsync();
        var dir = NewDir();
        var db = NewDb(dir);
        var junk = Path.Combine(dir, "broken.mp4");
        File.WriteAllText(junk, "not media");
        using var svc = new ImportService(db, Fake(ffmpeg));
        var job = svc.EnqueueFile(junk, new ImportOptions(), dir);
        await job.Completion.Task;
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.False(string.IsNullOrWhiteSpace(job.Message));
        Assert.Null(job.ItemId);
        Assert.Empty(db.ListMeetings());
    }

    [Fact]
    public async Task JobsRunOneAtATime_InOrder_AndTheIdleCallbackFiresWhenTheQueueDrains()
    {
        var ffmpeg = await FfmpegAsync();
        var dir = NewDir();
        var media = await MakeMediaAsync(ffmpeg, Path.Combine(dir, "a.wav"), video: false);
        int running = 0, maxRunning = 0, idle = 0;
        var order = new List<string>();
        Task<MediaImporter> Slow() => Task.FromResult(new MediaImporter(ffmpeg, (_, _, _) =>
        {
            maxRunning = Math.Max(maxRunning, Interlocked.Increment(ref running));
            Thread.Sleep(150);
            Interlocked.Decrement(ref running);
            return new[] { new WhisperSegment(0, 1, "x", "en") };
        }));
        using var svc = new ImportService(NewDb(dir), Slow, onIdle: () => Interlocked.Increment(ref idle));
        svc.JobChanged += j => { if (j.Status == JobStatus.Working) lock (order) order.Add(j.Name); };

        var copies = new[] { "first.wav", "second.wav", "third.wav" }.Select(n => { var p = Path.Combine(dir, n); File.Copy(media, p); return svc.EnqueueFile(p, new ImportOptions(), dir); }).ToList();
        foreach (var j in copies) await j.Completion.Task;
        Assert.True(await Eventually(() => idle >= 1));
        Assert.Equal(1, maxRunning);
        lock (order) Assert.Equal(new[] { "first.wav", "second.wav", "third.wav" }, order.Distinct());
        Assert.Equal(1, idle);                              // once, after the last job: the caller frees the model then
        Assert.Equal(3, svc.Jobs.Count);
    }

    [Fact]
    public async Task TheWatchedFolder_FeedsTheQueue_FinishedFilesMoveOn_FailedOnesGoToErrors()
    {
        var ffmpeg = await FfmpegAsync();
        var dir = NewDir();
        var db = NewDb(dir);
        var paths = WatchPaths.For(Path.Combine(dir, "Drop", "To Process"));
        using var svc = new ImportService(db, Fake(ffmpeg));
        using var watch = new WatchFolder(paths, (file, outDir) => svc.ImportForWatchAsync(file, outDir, new ImportOptions()), TimeSpan.FromMilliseconds(60));
        watch.Start();

        var good = await MakeMediaAsync(ffmpeg, Path.Combine(dir, "good.wav"), video: false);
        File.Move(good, Path.Combine(paths.Incoming, "good.wav"));
        File.WriteAllText(Path.Combine(paths.Incoming, "bad.mp4"), "not media");

        Assert.True(await Eventually(() => File.Exists(Path.Combine(paths.Processed, "good.wav")) && File.Exists(Path.Combine(paths.Errors, "bad.mp4"))));
        Assert.True(File.Exists(Path.Combine(paths.Output, "good.txt")));
        Assert.Equal("imported words\n", File.ReadAllText(Path.Combine(paths.Output, "good.txt")));
        Assert.True(File.Exists(Path.Combine(paths.Errors, "bad.mp4.error.log")));
        Assert.Equal("good", Assert.Single(db.ListMeetings()).Title);
    }

    [Fact]
    public async Task AYouTubeLink_BecomesAnItemOfThatKind_WithItsTitleAndSource()
    {
        var ffmpeg = await FfmpegAsync();
        var dir = NewDir();
        var db = NewDb(dir);
        const string info = """{"id":"dQw4w9WgXcQ","title":"Great talk","duration":60,"language":"en","subtitles":{"en":[{}]},"automatic_captions":{}}""";
        Task<(int, string)> Run(string args)
        {
            if (args.Contains("--dump-single-json")) return Task.FromResult((0, info));
            var folder = System.Text.RegularExpressions.Regex.Match(args, @"-o ""(?<d>[^""]+)\\%\(id\)s").Groups["d"].Value;
            File.WriteAllText(Path.Combine(folder, "dQw4w9WgXcQ.en.vtt"), "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nwelcome to the talk\n");
            return Task.FromResult((0, ""));
        }
        using var svc = new ImportService(db, Fake(ffmpeg), () => Task.FromResult(new YouTubeImporter("yt-dlp", null, Run, () => false)));
        var job = svc.EnqueueYouTube("https://youtu.be/dQw4w9WgXcQ", new ImportOptions(Language: "en"), Path.Combine(dir, "out"));
        await job.Completion.Task;

        Assert.Equal(JobStatus.Done, job.Status);
        Assert.Equal("Official captions", job.Message);
        var item = db.GetMeeting(job.ItemId!.Value)!;
        Assert.Equal(("youtube", "Great talk", "https://youtu.be/dQw4w9WgXcQ"), (item.Kind, item.Title, item.Source));
        Assert.Equal("welcome to the talk", Assert.Single(db.GetTranscript(item.Id)).Text);
    }

    [Fact]
    public async Task YouTubeFailures_ShowTheFriendlyMessage()
    {
        var ffmpeg = await FfmpegAsync();
        var dir = NewDir();
        using var svc = new ImportService(NewDb(dir), Fake(ffmpeg),
            () => Task.FromResult(new YouTubeImporter("yt-dlp", null, _ => Task.FromResult((1, "ERROR: Video unavailable")), () => true)));
        var job = svc.EnqueueYouTube("dQw4w9WgXcQ", new ImportOptions(), dir);
        await job.Completion.Task;
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("This video is unavailable.", job.Message);
    }

    [Fact]
    public async Task BadLinks_AreRefusedImmediately()
    {
        var ffmpeg = await FfmpegAsync();
        var dir = NewDir();
        using var withYt = new ImportService(NewDb(dir), Fake(ffmpeg), () => Task.FromResult(new YouTubeImporter("yt-dlp", null)));
        Assert.Throws<ArgumentException>(() => withYt.EnqueueYouTube("https://vimeo.com/1", new ImportOptions(), dir));
        using var without = new ImportService(NewDb(dir), Fake(ffmpeg));
        Assert.Throws<InvalidOperationException>(() => without.EnqueueYouTube("dQw4w9WgXcQ", new ImportOptions(), dir));
    }
}
