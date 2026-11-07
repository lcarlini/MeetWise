using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using MeetWise.Audio;
using MeetWise.Core.Hardware;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;
using MeetWise.Intelligence;
using MeetWise.Speech;
using MeetWise.Storage;

namespace MeetWise.App;

/// <summary>Everything the Studio pages share: settings, database, the recording and import engines, the AI engine, and navigation between pages.</summary>
public sealed class StudioContext
{
    public required SettingsStore Store { get; init; }
    public required AppSettings Settings { get; init; }
    public required HardwareProfile Hardware { get; init; }
    public required MeetingDatabase Db { get; init; }
    public required HttpClient Http { get; init; }
    public required SecretStore Secrets { get; init; }
    public required LlmFactory Llm { get; init; }
    public required RecordingSession Session { get; init; }
    public required EnhancementController Enhance { get; init; }
    public required ImportService Imports { get; init; }
    public Glossary Glossary { get; set; } = new(Array.Empty<string>());

    /// <summary>Switches page: record, subtitles, assistant, library or meetings.</summary>
    public Action<string> Navigate { get; set; } = _ => { };

    /// <summary>The recording or import the Subtitles and AI pages are working on.</summary>
    public long CurrentItemId { get; private set; }

    /// <summary>Lines picked on the Subtitles page; the AI page works on them instead of the whole recording when there are any.</summary>
    public IReadOnlyList<SubtitleCue> SelectedCues { get; set; } = Array.Empty<SubtitleCue>();

    public event Action<long>? ItemOpened;
    public event Action? ItemsChanged;
    public event Action<string>? Toast;
    public event Action<MeetWise.Intelligence.TranslatedCaption>? TranslationLine;

    /// <summary>A translated caption from live translation; the Record page shows it under the live subtitles.</summary>
    public void RaiseTranslation(MeetWise.Intelligence.TranslatedCaption caption) => TranslationLine?.Invoke(caption);

    public void OpenItem(long id, string page)
    {
        CurrentItemId = id;
        SelectedCues = Array.Empty<SubtitleCue>();
        ItemOpened?.Invoke(id);
        Navigate(page);
    }

    /// <summary>Records which item the pages are showing without switching page.</summary>
    public void SetCurrent(long id) => CurrentItemId = id;

    public void NotifyItemsChanged() => ItemsChanged?.Invoke();
    public void ShowToast(string text) => Toast?.Invoke(text);

    public UserProfile Profile => new(Settings.UserNickname, Settings.UserOccupation, Settings.UserContext);

    public void Save() => Store.Save(Settings);

    /// <summary>The microphone to record: the cleaned MeetWise Microphone while the clean-up route runs, otherwise the chosen one.</summary>
    public string RecorderMicId() => Enhance.IsRunning && VirtualMic.FindMic() is { } vm ? vm.Id : Settings.MicrophoneId;

    private bool _gpuPackAsked;

    /// <summary>On NVIDIA cards, offers the 400 MB GPU acceleration pack once per session. Declining keeps everything working on the CPU.</summary>
    public async Task OfferGpuPackAsync(IProgress<string>? status = null)
    {
        if (Hardware.Tier != HardwareTier.A_NvidiaRtx || CudaRuntimePack.IsInstalled() || _gpuPackAsked) return;
        _gpuPackAsked = true;
        var ask = MessageBox.Show("Install the GPU acceleration pack (about 400 MB from NVIDIA)? Subtitles, imports and the local AI run several times faster on your graphics card. Choose No to use the CPU.",
            "MeetWise", MessageBoxButton.YesNo);
        if (ask == MessageBoxResult.Yes)
            await CudaRuntimePack.EnsureAsync(Http, new Progress<double>(p => status?.Report($"Downloading GPU acceleration pack… {p:P0}")));
    }

    public async Task<ILlmProvider> GetLlmAsync(IProgress<string>? status = null)
    {
        if (Settings.CloudProvider is null) await OfferGpuPackAsync(status);
        return await Llm.GetAsync(Settings, Hardware, new Progress<double>(p => status?.Report($"Downloading the local AI model… {p:P0} (one time)")));
    }
}

/// <summary>One subtitle line in a list. Changes notify the UI so a line that is still being spoken can be updated in place.</summary>
public sealed class SubtitleRow : INotifyPropertyChanged
{
    private string _text = "";
    private string _speaker = "";
    private bool _selected;

    public long SegmentId { get; init; }
    public double Start { get; init; }
    public double End { get; set; }
    public string? Language { get; init; }
    public bool IsPartial { get; set; }

    public string Time => TimeSpan.FromSeconds(Start).ToString(Start >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");

    public string Speaker { get => _speaker; set { _speaker = value; Raise(nameof(Speaker)); } }
    public string Text { get => _text; set { _text = value; Raise(nameof(Text)); } }
    public bool IsSelected { get => _selected; set { _selected = value; Raise(nameof(IsSelected)); } }

    public SubtitleCue ToCue() => new(Start, End, Text, Speaker, Language);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
