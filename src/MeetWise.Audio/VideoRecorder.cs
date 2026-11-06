using System.Diagnostics;
using System.Runtime.InteropServices;
using MeetWise.Core.Logging;
using MeetWise.Core.Models;

namespace MeetWise.Audio;

public sealed record VideoEncoder(string Codec, string Name, string[] Args);

/// <summary>
/// Screen capture for the audio+video preset. Video is written as MKV (survives a crash), the two audio channels are already
/// being recorded as WAV by DualChannelRecorder, and <see cref="MuxAsync"/> joins them into one MP4 when the meeting ends.
/// </summary>
public sealed class VideoRecorder : IDisposable
{
    private static readonly VideoEncoder[] Hardware =
    {
        new("h264_nvenc", "NVIDIA NVENC", new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "30", "-b:v", "0" }),
        new("h264_qsv", "Intel Quick Sync", new[] { "-c:v", "h264_qsv", "-global_quality", "30" }),
        new("h264_amf", "AMD AMF", new[] { "-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "28", "-qp_p", "30" })
    };

    public static readonly VideoEncoder Software = new("libx264", "Software (x264)", new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "28" });

    private readonly string _ffmpeg;
    private Process? _process;
    private string? _stderrTail;

    public string? OutputPath { get; private set; }
    public DateTime StartedUtc { get; private set; }
    public VideoEncoder? Encoder { get; private set; }
    public bool IsRecording => _process is { HasExited: false };

    public VideoRecorder(string ffmpegExe) => _ffmpeg = ffmpegExe;

    /// <summary>Hardware encoders list every GPU vendor even when absent, so each one is checked with a real 15-frame test encode.</summary>
    public static async Task<IReadOnlyList<VideoEncoder>> ProbeEncodersAsync(string ffmpeg)
    {
        var list = new List<VideoEncoder>();
        string encoders;
        try { encoders = (await FFmpegPack.RunAsync(ffmpeg, "-hide_banner -encoders")).Output; }
        catch (Exception ex) { AppLog.Warn("Encoder listing failed: " + ex.Message); return new[] { Software }; }

        foreach (var hw in Hardware)
        {
            if (!encoders.Contains(hw.Codec, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var args = "-hide_banner -loglevel error -f lavfi -i color=c=black:s=1280x720:r=30 -frames:v 15 " + string.Join(' ', hw.Args) + " -pix_fmt yuv420p -f null -";
                if ((await FFmpegPack.RunAsync(ffmpeg, args)).ExitCode == 0) list.Add(hw);
            }
            catch (Exception ex) { AppLog.Info($"Encoder {hw.Codec} unavailable: {ex.Message}"); }
        }
        list.Add(Software);
        return list;
    }

    [DllImport("user32")] private static extern int GetSystemMetrics(int index);

    public static (int Width, int Height) PrimaryScreenSize()
    {
        // Even dimensions are required by yuv420p H.264.
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        return (w & ~1, h & ~1);
    }

    public void Start(string outputMkv, VideoEncoder encoder, int fps = 30, bool drawMouse = true)
    {
        if (IsRecording) throw new InvalidOperationException("Already recording video.");
        var (w, h) = PrimaryScreenSize();
        OutputPath = outputMkv;
        Encoder = encoder;
        var args = $"-hide_banner -loglevel error -f gdigrab -framerate {fps} -draw_mouse {(drawMouse ? 1 : 0)} " +
                   $"-offset_x 0 -offset_y 0 -video_size {w}x{h} -i desktop " +
                   string.Join(' ', encoder.Args) + " -pix_fmt yuv420p -f matroska \"" + outputMkv + "\"";
        var psi = new ProcessStartInfo(_ffmpeg, args)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        StartedUtc = DateTime.UtcNow;
        _process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _stderrTail = e.Data; };
        _process.BeginErrorReadLine();
        AppLog.Info($"Video started with {encoder.Name}: {outputMkv}");
    }

    /// <summary>Asks FFmpeg to finish the file cleanly ('q'), and kills it if it does not stop in time.</summary>
    public async Task StopAsync(int timeoutMs = 10_000)
    {
        var p = _process;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                await p.StandardInput.WriteAsync("q");
                await p.StandardInput.FlushAsync();
                using var cts = new CancellationTokenSource(timeoutMs);
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { p.Kill(true); }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
        }
        if (p.ExitCode != 0 && _stderrTail is not null) AppLog.Warn("FFmpeg ended with: " + _stderrTail);
        _process = null;
    }

    /// <summary>
    /// Joins the video with the microphone and system audio into an MP4. Audio starts slightly after the video process,
    /// so it is delayed by the measured start difference to keep lip-sync. The MKV is removed only after the MP4 is verified.
    /// </summary>
    public static async Task<string> MuxAsync(string ffmpeg, string mkv, string? micWav, string? loopWav, DateTime videoStartUtc,
        DateTime audioStartUtc, string outputMp4)
    {
        var inputs = new List<(string Path, double Delay)>();
        double delay = Math.Max(0, (audioStartUtc - videoStartUtc).TotalSeconds);
        if (micWav is not null && File.Exists(micWav)) inputs.Add((micWav, delay));
        if (loopWav is not null && File.Exists(loopWav)) inputs.Add((loopWav, delay));

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", Q(mkv) };
        foreach (var (path, _) in inputs) args.AddRange(new[] { "-i", Q(path) });
        if (inputs.Count == 0)
        {
            args.AddRange(new[] { "-c", "copy" });
        }
        else
        {
            var chains = inputs.Select((x, i) => $"[{i + 1}:a]aresample=48000,adelay={(int)(x.Delay * 1000)}:all=1[a{i}]");
            var mix = inputs.Count == 1 ? "[a0]anull[aout]"
                : string.Join("", inputs.Select((_, i) => $"[a{i}]")) + $"amix=inputs={inputs.Count}:duration=longest:normalize=0[aout]";
            args.AddRange(new[] { "-filter_complex", Q(string.Join(";", chains) + ";" + mix), "-map", "0:v", "-map", "[aout]", "-c:v", "copy", "-c:a", "aac", "-b:a", "160k" });
        }
        args.AddRange(new[] { "-movflags", "+faststart", Q(outputMp4) });

        var (code, output) = await FFmpegPack.RunAsync(ffmpeg, string.Join(' ', args), 10 * 60_000);
        if (code != 0 || !File.Exists(outputMp4) || new FileInfo(outputMp4).Length == 0)
            throw new InvalidOperationException("Could not create the MP4: " + output.Trim());

        File.Delete(mkv);
        return outputMp4;

        static string Q(string s) => "\"" + s + "\"";
    }

    /// <summary>
    /// Finds captures whose recording never finished (crash, power loss) and turns them into MP4s, together with their audio files.
    /// Files modified in the last <paramref name="settleSeconds"/> are skipped because a recording may still be writing them.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RecoverOrphansAsync(string ffmpeg, string folder, int settleSeconds = 30)
    {
        var recovered = new List<string>();
        if (!Directory.Exists(folder)) return recovered;
        foreach (var mkv in Directory.GetFiles(folder, "*.video.mkv"))
        {
            var baseName = Path.GetFileName(mkv)[..^".video.mkv".Length];
            var mp4 = Path.Combine(folder, baseName + ".mp4");
            if (File.Exists(mp4) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(mkv)).TotalSeconds < settleSeconds) continue;
            var mic = Path.Combine(folder, baseName + ".mic.wav");
            var loop = Path.Combine(folder, baseName + ".loop.wav");
            try
            {
                await MuxAsync(ffmpeg, mkv, File.Exists(mic) ? mic : null, File.Exists(loop) ? loop : null,
                    File.GetCreationTimeUtc(mkv), File.Exists(mic) ? File.GetCreationTimeUtc(mic) : File.GetCreationTimeUtc(mkv), mp4);
                recovered.Add(mp4);
            }
            catch (InvalidOperationException ex)
            {
                AppLog.Warn($"Could not recover {Path.GetFileName(mkv)}: {ex.Message}"); // keep the MKV for manual recovery
            }
        }
        return recovered;
    }

    public void Dispose()
    {
        try { _process?.Kill(true); } catch (Exception) { }
        _process?.Dispose();
    }
}
