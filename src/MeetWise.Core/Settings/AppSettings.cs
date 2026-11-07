namespace MeetWise.Core.Settings;

public enum CapturePreset { TranscriptOnly, AudioOnly, AudioVideo }
public enum QualityPreset { LowFileSize, Balanced, MaximumQuality }
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

    /// <summary>Translate other participants' speech into this language as live captions (null = off).</summary>
    public string? CaptionTranslateTo { get; set; }

    /// <summary>Speak my own speech, translated to this language ("same" = re-voice in a neutral accent), into SpeakDeviceId (null = off).</summary>
    public string? SpeakTranslationTo { get; set; }
    public string? SpeakDeviceId { get; set; }

    /// <summary>How long live translation waits for an unfinished sentence before translating it anyway. Lower is faster, higher gives the model whole sentences.</summary>
    public int TranslationHoldMs { get; set; } = 1200;

    /// <summary>When a cloud model fails, try the local model for live translation if it is already installed. Local processing never sends anything anywhere.</summary>
    public bool TranslationFallbackToLocal { get; set; } = true;

    /// <summary>The physical microphone to clean. Never the virtual MeetWise Microphone (that would create a feedback loop).</summary>
    public string? PhysicalMicId { get; set; }

    /// <summary>Keep the physical mic routed into the MeetWise Microphone whenever the app runs.</summary>
    public bool AlwaysOnMicrophone { get; set; }

    public bool StartWithWindows { get; set; }

    /// <summary>The Windows default microphones from before MeetWise Microphone was made the default, so they can be restored.</summary>
    public string? PreviousDefaultConsole { get; set; }
    public string? PreviousDefaultMultimedia { get; set; }
    public string? PreviousDefaultCommunications { get; set; }

    /// <summary>English speaking coach: private corrections on screen.</summary>
    public bool CoachEnabled { get; set; }
    public bool CoachOverlay { get; set; } = true;

    // ---- Studio (recording, subtitles, imports) ----

    /// <summary>GDI name of the monitor to record (for example \.\DISPLAY1); the primary one when empty or unplugged.</summary>
    public string MonitorDeviceName { get; set; } = "";
    public QualityPreset Quality { get; set; } = QualityPreset.Balanced;
    public bool RecordMicrophone { get; set; } = true;
    public bool RecordSystemAudio { get; set; } = true;

    /// <summary>Language of the subtitles: "auto" or an ISO code (the 20 languages of the subtitle extractor).</summary>
    public string SubtitleLanguage { get; set; } = "auto";
    public bool SubtitlesInEnglish { get; set; }
    public bool SaveSrt { get; set; } = true;
    public bool FloatingCaptions { get; set; }

    /// <summary>Interface language of the Studio pages: en, pt or es.</summary>
    public string UiLanguage { get; set; } = "en";

    /// <summary>Keep MeetWise's own windows out of screen recordings (like RecBox). Switch off to record MeetWise itself.</summary>
    public bool HideWindowFromCapture { get; set; } = true;

    public bool ImportNoiseFilter { get; set; }
    public bool WatchFolderEnabled { get; set; }
    public string WatchFolder { get; set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MeetWise", "To Process");

    public string TemplateId { get; set; } = "general";
    public string? UserNickname { get; set; }
    public string? UserOccupation { get; set; }
    public string? UserContext { get; set; }

    /// <summary>One custom vocabulary term per entry (names, acronyms, product words).</summary>
    public List<string> Glossary { get; set; } = new();

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
