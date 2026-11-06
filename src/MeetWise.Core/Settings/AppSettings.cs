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

    /// <summary>Base URL for an OpenAI-compatible server (for example http://localhost:11434/v1 for Ollama). Used when CloudProvider is "openai-compatible".</summary>
    public string? LlmBaseUrl { get; set; }
    public string? LlmModel { get; set; }

    /// <summary>Optional URL that receives action items as JSON.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Calendar feed in iCalendar format (Google "secret address in iCal format", Outlook published calendar, webcal://).</summary>
    public string? CalendarUrl { get; set; }
    public int ReminderMinutes { get; set; } = 5;
    public bool AutoDetectMeetings { get; set; } = true;

    public string TemplateId { get; set; } = "general";
    public string? UserNickname { get; set; }
    public string? UserOccupation { get; set; }
    public string? UserContext { get; set; }

    /// <summary>One custom vocabulary term per entry (names, acronyms, product words).</summary>
    public List<string> Glossary { get; set; } = new();

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
