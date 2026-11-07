using System.Runtime.InteropServices;
using MeetWise.Audio;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;

namespace MeetWise.Tests;

public class CaptureTests
{
    private static async Task<(string ffmpeg, string ffprobe)> ToolsAsync()
    {
        var exe = await FFmpegPack.EnsureAsync(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
        return (exe, FFmpegPack.FindProbe(exe)!);
    }

    // ---- monitors ----

    [Fact]
    public void Monitors_AreInNativePixels_WithEvenSizes_AndOnePrimary()
    {
        var monitors = new MonitorService().GetMonitors();
        Assert.NotEmpty(monitors);
        Assert.Single(monitors, m => m.IsPrimary);
        foreach (var m in monitors)
        {
            Assert.True(m.Width % 2 == 0 && m.Height % 2 == 0, "H.264 4:2:0 needs even dimensions");
            var dm = new MonitorService.Native.DEVMODE { dmSize = (short)Marshal.SizeOf<MonitorService.Native.DEVMODE>() };
            Assert.True(MonitorService.Native.EnumDisplaySettings(m.DeviceName, MonitorService.Native.EnumCurrentSettings, ref dm));
            Assert.Equal(dm.dmPelsWidth & ~1, m.Width);   // the real mode, not the DPI-virtualised one
            Assert.Equal(dm.dmPelsHeight & ~1, m.Height);
            Assert.Contains("×", m.DisplayName);
        }
    }

    [Fact]
    public void Find_FallsBackToThePrimary_WhenTheRememberedMonitorIsGone()
    {
        var svc = new MonitorService();
        var primary = svc.GetMonitors().First(m => m.IsPrimary);
        Assert.Equal(primary.DeviceName, svc.Find(@"\\.\DISPLAY99")!.DeviceName);
        Assert.Equal(primary.DeviceName, svc.Find(null)!.DeviceName);
        Assert.Equal(primary.DeviceName, svc.Find(primary.DeviceName.ToLowerInvariant())!.DeviceName);
    }

    // ---- quality presets ----

    [Theory]
    [InlineData("h264_nvenc")]
    [InlineData("h264_qsv")]
    [InlineData("h264_amf")]
    [InlineData("libx264")]
    public void Presets_RaiseQualityInOrder_ForEveryEncoder(string codec)
    {
        var enc = new VideoEncoder(codec, codec, Array.Empty<string>());
        string Value(QualityPreset q, string flag) { var a = VideoRecorder.ArgsFor(enc, q); return a[Array.IndexOf(a, flag) + 1]; }
        var flag = codec switch { "h264_nvenc" => "-cq", "h264_qsv" => "-global_quality", "h264_amf" => "-qp_i", _ => "-crf" };
        // a lower number means higher quality
        Assert.True(int.Parse(Value(QualityPreset.LowFileSize, flag)) > int.Parse(Value(QualityPreset.Balanced, flag)));
        Assert.True(int.Parse(Value(QualityPreset.Balanced, flag)) > int.Parse(Value(QualityPreset.MaximumQuality, flag)));
        Assert.Equal(codec, VideoRecorder.ArgsFor(enc, QualityPreset.Balanced)[1]);
    }

    [Fact]
    public async Task EveryPreset_EncodesATestClip_WithEveryEncoderThisPcHas()
    {
        var (ffmpeg, _) = await ToolsAsync();
        var encoders = await VideoRecorder.ProbeEncodersAsync(ffmpeg);
        Assert.Equal("libx264", encoders[^1].Codec);
        foreach (var enc in encoders)
            foreach (var q in Enum.GetValues<QualityPreset>())
            {
                var args = "-hide_banner -loglevel error -f lavfi -i testsrc2=s=1280x720:r=30 -frames:v 30 " + string.Join(' ', VideoRecorder.ArgsFor(enc, q)) + " -f null -";
                var (code, output) = await FFmpegPack.RunAsync(ffmpeg, args);
                Assert.True(code == 0, $"{enc.Name} / {q} failed: {output}");
            }
    }

    [Fact]
    public async Task LowFileSize_ProducesASmallerFile_ThanMaximumQuality()
    {
        var (ffmpeg, _) = await ToolsAsync();
        var dir = Path.Combine(Path.GetTempPath(), "mw-q-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        long Size(QualityPreset q)
        {
            var file = Path.Combine(dir, q + ".mkv");
            var args = "-hide_banner -loglevel error -y -f lavfi -i testsrc2=s=1280x720:r=30 -t 3 " + string.Join(' ', VideoRecorder.ArgsFor(VideoRecorder.Software, q)) + $" \"{file}\"";
            Assert.Equal(0, FFmpegPack.RunAsync(ffmpeg, args).GetAwaiter().GetResult().ExitCode);
            return new FileInfo(file).Length;
        }
        Assert.True(Size(QualityPreset.LowFileSize) < Size(QualityPreset.MaximumQuality));
    }

    [Fact]
    public async Task RecordingAChosenMonitor_ProducesAFileOfThatMonitorsSize()
    {
        var (ffmpeg, ffprobe) = await ToolsAsync();
        var monitor = new MonitorService().GetMonitors().First(m => m.IsPrimary);
        var encoders = await VideoRecorder.ProbeEncodersAsync(ffmpeg);
        var dir = Path.Combine(Path.GetTempPath(), "mw-mon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var video = new VideoRecorder(ffmpeg);
        var mkv = Path.Combine(dir, "m.mkv");
        video.Start(mkv, encoders[0], monitor: monitor, quality: QualityPreset.LowFileSize);
        await Task.Delay(3000);
        Assert.True(video.IsRecording, "FFmpeg exited early");
        await video.StopAsync();
        var (code, output) = await FFmpegPack.RunAsync(ffprobe, $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"{mkv}\"");
        Assert.Equal(0, code);
        Assert.Equal($"{monitor.Width},{monitor.Height}", output.Trim().Split('\n')[0].Trim());
    }

    // ---- guards ----

    [Fact]
    public void DiskGuard_BlocksStartingBelowOneGigabyte_AndStopsBelow400Megabytes()
    {
        Assert.Null(CaptureGuards.CheckCanStart("C:\\", _ => 5L * 1024 * 1024 * 1024));
        var msg = CaptureGuards.CheckCanStart("C:\\", _ => 300L * 1024 * 1024);
        Assert.NotNull(msg);
        Assert.Contains("300 MB", msg);
        Assert.Null(CaptureGuards.CheckCanStart("C:\\", _ => -1)); // unknown space never blocks a recording

        Assert.False(CaptureGuards.ShouldStopForSpace("C:\\", _ => 500L * 1024 * 1024));
        Assert.True(CaptureGuards.ShouldStopForSpace("C:\\", _ => 399L * 1024 * 1024));
        Assert.False(CaptureGuards.ShouldStopForSpace("C:\\", _ => -1));
    }

    [Fact]
    public void FreeBytes_ReadsTheRealDisk() => Assert.True(CaptureGuards.FreeBytes(Path.GetTempPath()) > 0);

    [Fact]
    public void KeepAwake_CanBeSwitchedOnAndOff_AndAnInvalidWindowIsRefused()
    {
        CaptureGuards.KeepAwake(true);
        CaptureGuards.KeepAwake(false);
        Assert.False(CaptureGuards.ExcludeFromCapture(IntPtr.Zero));
    }
}
