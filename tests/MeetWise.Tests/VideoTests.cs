using System.Text.Json.Nodes;
using MeetWise.Audio;
using MeetWise.Core.Models;

namespace MeetWise.Tests;

public class VideoTests
{
    private static async Task<(string ffmpeg, string ffprobe)> ToolsAsync()
    {
        // First run downloads the pinned, hash-verified FFmpeg build; later runs reuse it.
        var exe = await FFmpegPack.EnsureAsync(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
        return (exe, FFmpegPack.FindProbe(exe)!);
    }

    private static async Task<JsonNode> ProbeAsync(string ffprobe, string file)
    {
        var (code, output) = await FFmpegPack.RunAsync(ffprobe, $"-v error -print_format json -show_streams -show_format \"{file}\"");
        Assert.Equal(0, code);
        return JsonNode.Parse(output[output.IndexOf('{')..])!;
    }

    private static void WriteTone(string path, double seconds, double hz)
    {
        using var w = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(48000, 16, 1));
        var s = new float[(int)(48000 * seconds)];
        for (int i = 0; i < s.Length; i++) s[i] = (float)(0.3 * Math.Sin(2 * Math.PI * hz * i / 48000));
        w.WriteSamples(s, 0, s.Length);
    }

    [Fact]
    public async Task Mux_JoinsVideoAndBothAudioChannels_IntoMp4()
    {
        var (ffmpeg, ffprobe) = await ToolsAsync();
        var dir = Path.Combine(Path.GetTempPath(), "mw-vid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var mkv = Path.Combine(dir, "v.mkv");
        var (c, o) = await FFmpegPack.RunAsync(ffmpeg, $"-hide_banner -loglevel error -f lavfi -i testsrc=s=640x360:r=30 -t 3 -c:v libx264 -pix_fmt yuv420p \"{mkv}\"");
        Assert.True(c == 0, o);
        WriteTone(Path.Combine(dir, "m.wav"), 3, 440);
        WriteTone(Path.Combine(dir, "l.wav"), 3, 880);

        var t0 = DateTime.UtcNow;
        var mp4 = await VideoRecorder.MuxAsync(ffmpeg, mkv, Path.Combine(dir, "m.wav"), Path.Combine(dir, "l.wav"), t0, t0.AddMilliseconds(250), Path.Combine(dir, "out.mp4"));

        Assert.False(File.Exists(mkv), "the MKV is removed only after the MP4 is verified");
        var info = await ProbeAsync(ffprobe, mp4);
        var streams = info["streams"]!.AsArray();
        Assert.Contains(streams, s => s!["codec_type"]!.ToString() == "video" && s["codec_name"]!.ToString() == "h264");
        Assert.Contains(streams, s => s!["codec_type"]!.ToString() == "audio" && s["codec_name"]!.ToString() == "aac");
        Assert.InRange(double.Parse(info["format"]!["duration"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture), 2.9, 3.6);
    }

    [Fact]
    public async Task Mux_FailsLoudly_AndKeepsTheMkv_WhenFfmpegFails()
    {
        var (ffmpeg, _) = await ToolsAsync();
        var dir = Path.Combine(Path.GetTempPath(), "mw-vid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bogus = Path.Combine(dir, "v.mkv");
        File.WriteAllText(bogus, "not a video");
        await Assert.ThrowsAsync<InvalidOperationException>(() => VideoRecorder.MuxAsync(ffmpeg, bogus, null, null, DateTime.UtcNow, DateTime.UtcNow, Path.Combine(dir, "o.mp4")));
        Assert.True(File.Exists(bogus));
    }

    [Fact]
    public async Task ScreenRecording_ProducesPlayableMp4_WithRealEncoder()
    {
        var (ffmpeg, ffprobe) = await ToolsAsync();
        var encoders = await VideoRecorder.ProbeEncodersAsync(ffmpeg);
        Assert.Equal("libx264", encoders[^1].Codec); // software is always the last fallback
        var dir = Path.Combine(Path.GetTempPath(), "mw-vid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        using var video = new VideoRecorder(ffmpeg);
        var mkv = Path.Combine(dir, "screen.mkv");
        video.Start(mkv, encoders[0]);
        await Task.Delay(3500);
        Assert.True(video.IsRecording, "FFmpeg exited early; screen capture is unavailable in this session");
        await video.StopAsync();
        Assert.False(video.IsRecording);

        WriteTone(Path.Combine(dir, "m.wav"), 3, 440);
        var mp4 = await VideoRecorder.MuxAsync(ffmpeg, mkv, Path.Combine(dir, "m.wav"), null, video.StartedUtc, video.StartedUtc.AddMilliseconds(300), Path.Combine(dir, "screen.mp4"));
        var info = await ProbeAsync(ffprobe, mp4);
        var v = info["streams"]!.AsArray().First(s => s!["codec_type"]!.ToString() == "video")!;
        var (w, _) = VideoRecorder.PrimaryScreenSize();
        Assert.Equal(w, int.Parse(v["width"]!.ToString()));
        Assert.InRange(double.Parse(info["format"]!["duration"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture), 2.5, 5.0);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "video-encoder.txt"), $"{encoders[0].Name}; screen {w}x{v["height"]}");
    }
}
