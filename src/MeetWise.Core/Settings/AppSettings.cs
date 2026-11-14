namespace MeetWise.Core.Settings;

public enum NoiseMode { Off, Light, Balanced, Max }
public enum HardwareTier { A_NvidiaRtx, B_AnyGpu, C_CpuOnly }

/// <summary>Who removes noise and room echo from the microphone. With NVIDIA Broadcast, MeetWise reads the "Microphone (NVIDIA Broadcast)"
/// and only adds what Broadcast does not do (Accent Assist, and optionally speaker-echo cancellation and automatic volume).</summary>
public enum CleanupEngine { MeetWise, NvidiaBroadcast }

public sealed class AppSettings
{
    /// <summary>Schema version of the settings file; see SettingsStore.Migrate. Files written before version 2 have no value (0).</summary>
    public int SettingsVersion { get; set; }

    /// <summary>Legacy id of the virtual microphone; kept so older settings files load.</summary>
    public string MicrophoneId { get; set; } = "default";

    public CleanupEngine Cleanup { get; set; } = CleanupEngine.MeetWise;

    /// <summary>Background noise removal (dogs, birds, keyboard, TV, room). Max is the 48 kHz full-band model.</summary>
    public NoiseMode NoiseCancellation { get; set; } = NoiseMode.Max;

    /// <summary>How much noise to remove, 0-100. 100 removes everything the model finds; lower keeps a little room tone (sounds more natural).</summary>
    public int NoiseStrength { get; set; } = 100;

    public bool EchoCancellation { get; set; } = true;

    /// <summary>Remove background voices (colleagues, TV dialogue, shouting): keeps only the enrolled speaker.</summary>
    public bool VoiceIsolation { get; set; }

    /// <summary>Keeps the voice at a steady level whether you lean in or sit back.</summary>
    public bool AutoGain { get; set; } = true;

    /// <summary>Phase 2: real-time accent assist (Brazilian Portuguese speaker → US English listeners).</summary>
    public bool AccentAssist { get; set; }

    /// <summary>How strongly the pronunciation is pulled towards the native bank, 0-100.</summary>
    public int AccentStrength { get; set; } = 60;

    /// <summary>Fixed delay the accent stage adds, in milliseconds (at least the worker's latency plus 60 ms).</summary>
    public int AccentBufferMs { get; set; } = 300;

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Null means auto-detect. Overrides the detected hardware tier.</summary>
    public HardwareTier? TierOverride { get; set; }

    /// <summary>The physical microphone to clean. Never the virtual MeetWise Microphone (that would create a feedback loop).</summary>
    public string? PhysicalMicId { get; set; }

    /// <summary>Keep the physical mic routed into the MeetWise Microphone whenever MeetWise runs. On by default: an unfed virtual mic is silent in Zoom.</summary>
    public bool AlwaysOnMicrophone { get; set; } = true;

    public bool StartWithWindows { get; set; } = true;

    /// <summary>The Windows default microphones from before MeetWise Microphone was made the default, so they can be restored.</summary>
    public string? PreviousDefaultConsole { get; set; }
    public string? PreviousDefaultMultimedia { get; set; }
    public string? PreviousDefaultCommunications { get; set; }

    /// <summary>Interface language: en or pt.</summary>
    public string UiLanguage { get; set; } = "en";

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
