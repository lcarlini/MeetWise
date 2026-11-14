namespace MeetWise.Core.Models;

/// <summary>
/// Where the optional Accent Assist pack lives: a Python environment with PyTorch (CUDA), the kNN-VC weights (WavLM-Large, prematched
/// HiFi-GAN) and the user's accent bank, all under %LocalAppData%\MeetWise\accent. The worker script ships with MeetWise (accent\worker.py).
/// </summary>
public static class AccentPack
{
    public static string Home => Environment.GetEnvironmentVariable("MEETWISE_ACCENT_HOME") is { Length: > 0 } h ? h : Path.Combine(AppPaths.Local, "accent");
    public static string Python => Path.Combine(Home, "venv", "Scripts", "python.exe");
    public static string WavLm => Path.Combine(Home, "weights", "WavLM-Large.pt");
    public static string Vocoder => Path.Combine(Home, "weights", "prematch_g_02500000.pt");
    public static string UserBank => Path.Combine(Home, "banks", "user.pt");
    public static string NativeBank => Path.Combine(Home, "banks", "native.pt");

    /// <summary>The bank to use: the user's own (native pronunciation in their voice) when built, otherwise the native speakers' bank.</summary>
    public static string? Bank => File.Exists(UserBank) ? UserBank : File.Exists(NativeBank) ? NativeBank : null;

    /// <summary>The worker script: next to the program when published, or in the repository's accent folder when run from a build.</summary>
    public static string? WorkerScript
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "accent", "worker.py");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }

    public static bool IsInstalled => File.Exists(Python) && File.Exists(WavLm) && File.Exists(Vocoder) && Bank is not null && WorkerScript is not null;

    /// <summary>What is missing, for the window (null when everything is there).</summary>
    public static string? Missing()
    {
        if (!File.Exists(Python)) return "the Python environment";
        if (!File.Exists(WavLm) || !File.Exists(Vocoder)) return "the model weights";
        if (Bank is null) return "your accent bank";
        if (WorkerScript is null) return "the worker script";
        return null;
    }
}
