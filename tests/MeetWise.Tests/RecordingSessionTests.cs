using System.Text.Json.Nodes;
using MeetWise.Audio;
using MeetWise.Core.Hardware;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;
using MeetWise.Speech;
using MeetWise.Storage;

namespace MeetWise.Tests;

public class RecordingSessionTests
{
    private static readonly HardwareProfile Hw = HardwareDetector.Detect();

    private static (RecordingSession Session, MeetingDatabase Db, string Dir) New()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-rs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = new MeetingDatabase(Path.Combine(dir, "db", "t.db"));
        return (new RecordingSession(db, new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, Hw), db, Path.Combine(dir, "out"));
    }

    private static RecordingOptions Options(string dir, CaptureMode mode = CaptureMode.SubtitlesOnly, bool mic = true, bool system = false, bool srt = true,
        QualityPreset quality = QualityPreset.LowFileSize, string language = "en", MonitorInfo? monitor = null)
        => new(mode, monitor, quality, mic, system, "default", language, false, srt, dir);

    private static bool Devices => new AudioDeviceService().GetMicrophones().Count > 1 && new AudioDeviceService().IsSystemAudioAvailable();

    // ---- pure logic ----

    [Fact]
    public void SubtitleOffset_IsHowMuchLaterTheAudioStarted_NeverNegative()
    {
        var v = new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(0.25, RecordingSession.SubtitleOffset(v.AddMilliseconds(250), v), 6);
        Assert.Equal(0, RecordingSession.SubtitleOffset(v.AddMilliseconds(-40), v));   // audio first: no negative shift
        Assert.Equal(0, RecordingSession.SubtitleOffset(v, null));                       // no video: nothing to line up with
    }

    [Fact]
    public void RequiredModels_DependOnTheLanguageChoice()
    {
        var o = Options("x");
        var english = RecordingSession.RequiredModels(o, Hw);
        Assert.Contains(ModelCatalog.StreamingEnglish, english);
        Assert.DoesNotContain(english, m => m.Kind == "whisper");

        foreach (var opts in new[] { o with { SubtitleLanguage = "pt" }, o with { SubtitleLanguage = "auto" }, o with { TranslateToEnglish = true } })
        {
            var models = RecordingSession.RequiredModels(opts, Hw);
            Assert.Contains(ModelCatalog.SileroVad, models);
            Assert.Contains(models, m => m.Kind == "whisper");
        }
    }

    [Fact]
    public async Task Start_RefusesWithoutAnyAudioSource_AndOnAFullDisk()
    {
        var (session, _, dir) = New();
        using var _ = session;
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(Options(dir, mic: false, system: false)));
        Assert.Contains("audio source", none.Message);

        session.FreeBytes = _ => 200L * 1024 * 1024;
        var full = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(Options(dir)));
        Assert.Contains("Not enough disk space", full.Message);
        Assert.False(session.IsRecording);
    }

    [Fact]
    public async Task Stop_WithoutARecording_IsAnError()
    {
        var (session, _, _) = New();
        using var _ = session;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopAsync());
    }

    [Fact]
    public void Peak_ReadsBothSampleFormats()
    {
        var floats = new byte[64];
        BitConverter.GetBytes(0.5f).CopyTo(floats, 0);
        Assert.Equal(0.5f, RecordingSession.Peak(floats, 64, true), 3);
        var ints = new byte[32];
        BitConverter.GetBytes((short)16384).CopyTo(ints, 0);
        Assert.Equal(0.5f, RecordingSession.Peak(ints, 32, false), 3);
        Assert.Equal(0f, RecordingSession.Peak(new byte[16], 16, true));
    }

    // ---- real recordings ----

    [Fact]
    public async Task SubtitlesOnly_RecordsTheMicrophone_SavesTextFiles_AndKeepsNoAudio()
    {
        if (!Devices) return;
        var (session, db, dir) = New();
        using var _ = session;
        var statuses = new List<string>();
        session.Status += statuses.Add;
        await session.StartAsync(Options(dir));
        Assert.True(session.IsRecording);
        await Task.Delay(3000);
        Assert.InRange(session.Elapsed.TotalSeconds, 2.5, 5);
        var result = await session.StopAsync();

        Assert.False(session.IsRecording);
        Assert.True(File.Exists(result.TxtPath));
        Assert.True(File.Exists(result.SrtPath!));
        Assert.Null(result.VideoPath);
        Assert.Null(result.AudioPath);
        var item = db.GetMeeting(result.ItemId)!;
        Assert.Equal("recording", item.Kind);
        Assert.Contains("Recording", statuses);
        await Task.Delay(1500); // the temporary audio is removed in the background
        Assert.Empty(Directory.GetFiles(dir, "*.wav"));
    }

    [Fact]
    public async Task VideoAndAudio_ProducesAnMp4_WithBothAudioChannels_AndSubtitlesThatLineUp()
    {
        if (!Devices) return;
        var (session, db, dir) = New();
        using var _ = session;
        var monitor = new MonitorService().Find(null)!;
        await session.StartAsync(Options(dir, CaptureMode.VideoAudio, mic: true, system: true, monitor: monitor));
        await Task.Delay(1500);
        session.InjectCue(new SubtitleCue(1.0, 2.5, "first subtitle of the demo", "Me", "en"));
        await Task.Delay(2500);
        session.InjectCue(new SubtitleCue(3.0, 4.0, "second subtitle", "Others", "en"));
        var result = await session.StopAsync();

        // the MP4 has a picture of the chosen monitor and audio
        Assert.True(File.Exists(result.VideoPath!), "mp4 missing: " + result.Notice);
        Assert.EndsWith(".mp4", result.VideoPath);
        var ffprobe = FFmpegPack.FindProbe(FFmpegPack.Find()!)!;
        var (code, output) = await FFmpegPack.RunAsync(ffprobe, $"-v error -print_format json -show_streams \"{result.VideoPath}\"");
        Assert.Equal(0, code);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "ffprobe-out.txt"), output);
        var streams = JsonNode.Parse(output[output.IndexOf('{')..(output.LastIndexOf('}') + 1)])!["streams"]!.AsArray();
        var video = streams.First(s => s!["codec_type"]!.ToString() == "video")!;
        Assert.Equal((monitor.Width, monitor.Height), (int.Parse(video["width"]!.ToString()), int.Parse(video["height"]!.ToString())));
        Assert.Contains(streams, s => s!["codec_type"]!.ToString() == "audio");
        Assert.False(File.Exists(Path.Combine(dir, "x.mkv")));
        Assert.Empty(Directory.GetFiles(dir, "*.video.mkv")); // the raw capture is removed once the MP4 is verified

        // subtitles: saved as they arrived, written next to the video, shifted by the audio start delay
        Assert.Equal(2, db.GetTranscript(result.ItemId).Count);
        Assert.Equal("recording", db.GetMeeting(result.ItemId)!.Kind);
        Assert.Equal(result.VideoPath, db.GetMeeting(result.ItemId)!.VideoPath);
        var srt = File.ReadAllText(result.SrtPath!);
        var parsed = SubtitleFormatter.Parse(srt);
        Assert.Equal(new[] { "first subtitle of the demo", "second subtitle" }, parsed.Select(c => c.Text));
        double shift = parsed[0].Start - 1.0;
        Assert.InRange(shift, 0, 2.0);                                    // the audio starts moments after the screen capture
        Assert.InRange(Math.Abs(shift - (parsed[1].Start - 3.0)), 0, 0.01);  // and every cue is shifted by the same amount
        Assert.InRange(Math.Abs(shift - db.GetMeeting(result.ItemId)!.SubtitleOffset), 0, 0.01); // stored too, so later exports line up with the video (SRT text is rounded to ms)
        Assert.Equal("first subtitle of the demo\nsecond subtitle\n", File.ReadAllText(result.TxtPath));
        Assert.Single(Directory.GetFiles(dir, "*.mic.wav"));            // audio is kept for refining
    }

    [Fact]
    public async Task AudioOnly_KeepsTheWavFiles()
    {
        if (!Devices) return;
        var (session, _, dir) = New();
        using var _ = session;
        await session.StartAsync(Options(dir, CaptureMode.AudioOnly, srt: false));
        await Task.Delay(2000);
        var result = await session.StopAsync();
        Assert.Null(result.SrtPath);                                      // .srt is optional
        Assert.True(File.Exists(result.TxtPath));
        Assert.True(File.Exists(result.AudioPath!));
        Assert.Null(result.VideoPath);
    }

    [Fact]
    public async Task ARecordingIsStoppedAndSavedAutomatically_WhenTheDiskRunsLow()
    {
        if (!Devices) return;
        var (session, _, dir) = New();
        using var _ = session;
        int calls = 0;
        session.FreeBytes = _ => ++calls <= 1 ? 5L * 1024 * 1024 * 1024 : 100L * 1024 * 1024; // plenty to start, then almost none
        session.WatchdogInterval = TimeSpan.FromMilliseconds(200);
        var failures = new List<string>();
        RecordingResult? stopped = null;
        session.Failed += failures.Add;
        session.Stopped += r => stopped = r;

        await session.StartAsync(Options(dir));
        for (int i = 0; i < 100 && stopped is null; i++) await Task.Delay(100);
        Assert.NotNull(stopped);
        Assert.Contains("disk is almost full", Assert.Single(failures));
        Assert.False(session.IsRecording);
        Assert.True(File.Exists(stopped!.TxtPath));
    }

    [Fact]
    public async Task OnlyOneRecordingAtATime_AndTheLevelMetersMove()
    {
        if (!Devices) return;
        var (session, _, dir) = New();
        using var _ = session;
        await session.StartAsync(Options(dir));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(Options(dir)));
        await Task.Delay(1200);
        var (mic, sys) = session.Levels;
        Assert.InRange(mic, 0, 1);
        Assert.InRange(sys, 0, 1);
        await session.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopAsync()); // already stopped
    }

    [Fact]
    public async Task TheCustomVocabularyFilter_IsAppliedToEverySubtitle_BeforeItIsSaved()
    {
        if (!Devices) return;
        var (session, db, dir) = New();
        using var _ = session;
        session.TextFilter = t => t.Replace("kubernetis", "Kubernetes");
        await session.StartAsync(Options(dir));
        session.InjectCue(new SubtitleCue(0.2, 1.2, "we deploy on kubernetis", "Me", "en"));
        session.InjectCue(new SubtitleCue(1.3, 1.5, "   ", "Me", "en")); // blank text is never saved
        var result = await session.StopAsync();
        Assert.Equal("we deploy on Kubernetes", Assert.Single(db.GetTranscript(result.ItemId)).Text);
        Assert.Contains("Kubernetes", File.ReadAllText(result.TxtPath));
    }
}
