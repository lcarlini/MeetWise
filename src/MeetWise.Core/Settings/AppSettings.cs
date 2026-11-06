namespace MeetWise.Core.Settings;

public enum CapturePreset { TranscriptOnly, AudioOnly, AudioVideo }
public enum NoiseMode { Off, Light, Balanced, Max }
public enum HardwareTier { A_NvidiaRtx, B_AnyGpu, C_CpuOnly }

public sealed class AppSettings
{
    public string MicrophoneId { get; set; } = "default";
    public string OutputFolder { get; set; } = AppPaths.DefaultRecordings;
    public CapturePreset Preset { get; set; } = CapturePreset.TranscriptOnly;
    public NoiseMode NoiseCancellation { get; set; } = NoiseMode.Balanced;
    public bool EchoCancellation { get; set; } = true;
    public bool VoiceIsolation { get; set; }
    public string SpokenLanguage { get; set; } = "auto";
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Null means auto-detect. Overrides the detected hardware tier.</summary>
    public HardwareTier? TierOverride { get; set; }

    /// <summary>Cloud provider id or null for fully local. API keys live in Credential Manager, never here.</summary>
    public string? CloudProvider { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
