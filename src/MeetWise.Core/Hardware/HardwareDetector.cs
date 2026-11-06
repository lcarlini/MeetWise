using System.Diagnostics;
using System.Globalization;
using MeetWise.Core.Settings;

namespace MeetWise.Core.Hardware;

public sealed record GpuInfo(string Name, long VramMb, bool IsNvidia);

public sealed record HardwareProfile(IReadOnlyList<GpuInfo> Gpus, int CpuThreads, HardwareTier Tier, string WhisperModel);

/// <summary>Detects NVIDIA GPU/VRAM via nvidia-smi (no native deps) and maps it to a tier and Whisper model.</summary>
public static class HardwareDetector
{
    public static HardwareProfile Detect(HardwareTier? tierOverride = null)
    {
        var gpus = QueryNvidia();
        var best = gpus.OrderByDescending(g => g.VramMb).FirstOrDefault();
        var tier = tierOverride ?? ClassifyTier(best, gpus.Count > 0);
        return new HardwareProfile(gpus, Environment.ProcessorCount, tier, SelectWhisperModel(best?.VramMb ?? 0));
    }

    public static HardwareTier ClassifyTier(GpuInfo? best, bool anyGpu)
    {
        if (best is { IsNvidia: true } && best.VramMb >= 6000) return HardwareTier.A_NvidiaRtx;
        return anyGpu ? HardwareTier.B_AnyGpu : HardwareTier.C_CpuOnly;
    }

    public static string SelectWhisperModel(long vramMb)
    {
        if (vramMb >= 8000) return "large-v3-turbo";
        if (vramMb >= 5000) return "medium";
        if (vramMb >= 3000) return "small";
        return "base";
    }

    private static List<GpuInfo> QueryNvidia()
    {
        var list = new List<GpuInfo>();
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return list;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(4000)) { p.Kill(); return list; }
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(',', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb))
                    list.Add(new GpuInfo(parts[0], mb, true));
            }
        }
        catch
        {
            // nvidia-smi absent: no NVIDIA GPU or driver installed.
        }
        return list;
    }
}
