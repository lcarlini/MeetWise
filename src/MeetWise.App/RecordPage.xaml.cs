using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MeetWise.Audio;
using MeetWise.Core;
using MeetWise.Core.Logging;
using MeetWise.Core.Settings;
using MeetWise.Speech;

namespace MeetWise.App;

/// <summary>Record a screen (like RecBox) while subtitles appear live; stopping saves the video, the audio and the subtitles.</summary>
public partial class RecordPage : UserControl
{
    private sealed record Choice<T>(T Value, string Text) { public override string ToString() => Text; }

    private readonly StudioContext _ctx;
    private readonly ObservableCollection<SubtitleRow> _rows = new();
    private readonly Dictionary<string, SubtitleRow> _partials = new();
    private readonly HashSet<string> _speaking = new();
    private readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private CaptionOverlayWindow? _captions;
    private RecordingResult? _last;
    private bool _busy;
    private int _blink;

    public RecordPage(StudioContext ctx)
    {
        InitializeComponent();
        _ctx = ctx;
        LiveList.ItemsSource = _rows;
        _rows.CollectionChanged += (_, _) => EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var s = ctx.Settings;
        MonitorCombo.ItemsSource = new MonitorService().GetMonitors();
        MonitorCombo.SelectedItem = ((IEnumerable<MonitorInfo>)MonitorCombo.ItemsSource).FirstOrDefault(m => m.DeviceName == s.MonitorDeviceName)
                                    ?? ((IEnumerable<MonitorInfo>)MonitorCombo.ItemsSource).FirstOrDefault(m => m.IsPrimary);
        var mics = new AudioDeviceService().GetMicrophones();
        MicCombo.ItemsSource = mics;
        MicCombo.SelectedItem = mics.FirstOrDefault(m => m.Id == s.MicrophoneId) ?? mics[0];
        UseMicCheck.IsChecked = s.RecordMicrophone;
        SystemAudioCheck.IsChecked = s.RecordSystemAudio;
        EnglishCheck.IsChecked = s.SubtitlesInEnglish;
        SrtCheck.IsChecked = s.SaveSrt;
        FloatingCheck.IsChecked = s.FloatingCaptions;
        FillChoices();
        Loc.Instance.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Loc.Language)) FillChoices(); };

        var d = Dispatcher;
        _ctx.TranslationLine += c => d.BeginInvoke(() =>
        {
            // a translation that failed still shows what was said, marked, so nothing the other side says is lost; "≈" marks one that looked doubtful
            var text = c.Translated is null ? c.Original : (c.Doubtful ? "≈ " : "") + c.Translated;
            var row = new SubtitleRow { Start = _ctx.Session.Elapsed.TotalSeconds, Speaker = c.Translated is null ? "⚠" : "→", Text = text };
            _rows.Add(row);
            LiveList.ScrollIntoView(row);
        });
        _ctx.Session.Cue += c => d.BeginInvoke(() => OnCue(c));
        _ctx.Session.Partial += (ch, t) => d.BeginInvoke(() => OnPartial(ch, t));
        _ctx.Session.Speaking += (ch, on) => d.BeginInvoke(() => OnSpeaking(ch, on));
        _ctx.Session.Status += t => d.BeginInvoke(() => { if (_ctx.Session.IsRecording || _busy) StatusText.Text = t; });
        _ctx.Session.Failed += t => d.BeginInvoke(() => StatusText.Text = t);
        _ctx.Session.Stopped += r => d.BeginInvoke(() => OnStopped(r));
        _ui.Tick += (_, _) => Tick();
        _ui.Start();
        SetVisual(false);
        Mode_Changed(null, null!);
    }

    /// <summary>Rebuilds the drop-downs whose texts depend on the interface language, keeping the user's choice.</summary>
    private void FillChoices()
    {
        var s = _ctx.Settings;
        var mode = ModeCombo.SelectedValue is CaptureMode m ? m : ToMode(s.Preset);
        var quality = QualityCombo.SelectedValue is QualityPreset q ? q : s.Quality;
        var lang = LanguageCombo.SelectedValue as string ?? s.SubtitleLanguage;

        ModeCombo.DisplayMemberPath = "Text"; ModeCombo.SelectedValuePath = "Value";
        ModeCombo.ItemsSource = new[]
        {
            new Choice<CaptureMode>(CaptureMode.VideoAudio, Loc.T("rec.mode.video")),
            new Choice<CaptureMode>(CaptureMode.AudioOnly, Loc.T("rec.mode.audio")),
            new Choice<CaptureMode>(CaptureMode.SubtitlesOnly, Loc.T("rec.mode.text"))
        };
        ModeCombo.SelectedValue = mode;

        QualityCombo.DisplayMemberPath = "Text"; QualityCombo.SelectedValuePath = "Value";
        QualityCombo.ItemsSource = new[]
        {
            new Choice<QualityPreset>(QualityPreset.LowFileSize, Loc.T("rec.q.low")),
            new Choice<QualityPreset>(QualityPreset.Balanced, Loc.T("rec.q.balanced")),
            new Choice<QualityPreset>(QualityPreset.MaximumQuality, Loc.T("rec.q.max"))
        };
        QualityCombo.SelectedValue = quality;

        LanguageCombo.DisplayMemberPath = "Text"; LanguageCombo.SelectedValuePath = "Value";
        LanguageCombo.ItemsSource = new[] { Languages.Auto }.Concat(Languages.All.Select(l => l.Code))
            .Select(c => new Choice<string>(c, Languages.Name(c, Loc.Instance.Language))).ToList();
        LanguageCombo.SelectedValue = Languages.IsKnown(lang) ? lang : Languages.Auto;
    }

    private static CaptureMode ToMode(CapturePreset p) => p switch { CapturePreset.AudioVideo => CaptureMode.VideoAudio, CapturePreset.AudioOnly => CaptureMode.AudioOnly, _ => CaptureMode.SubtitlesOnly };
    private static CapturePreset ToPreset(CaptureMode m) => m switch { CaptureMode.VideoAudio => CapturePreset.AudioVideo, CaptureMode.AudioOnly => CapturePreset.AudioOnly, _ => CapturePreset.TranscriptOnly };

    private void Mode_Changed(object? sender, SelectionChangedEventArgs? e)
    {
        if (MonitorCombo is null || QualityCombo is null) return;
        bool video = ModeCombo.SelectedValue is CaptureMode.VideoAudio;
        MonitorCombo.IsEnabled = QualityCombo.IsEnabled = video;
        QualityLabel.Opacity = video ? 1 : 0.4;
    }

    private RecordingOptions BuildOptions()
    {
        var s = _ctx.Settings;
        var mode = ModeCombo.SelectedValue is CaptureMode m ? m : CaptureMode.VideoAudio;
        var monitor = MonitorCombo.SelectedItem as MonitorInfo;
        if (MicCombo.SelectedItem is AudioDeviceInfo mic) s.MicrophoneId = mic.Id;
        s.Preset = ToPreset(mode);
        s.MonitorDeviceName = monitor?.DeviceName ?? "";
        s.Quality = QualityCombo.SelectedValue is QualityPreset q ? q : QualityPreset.Balanced;
        s.RecordMicrophone = UseMicCheck.IsChecked == true;
        s.RecordSystemAudio = SystemAudioCheck.IsChecked == true;
        s.SubtitleLanguage = LanguageCombo.SelectedValue as string ?? Languages.Auto;
        s.SubtitlesInEnglish = EnglishCheck.IsChecked == true;
        s.SaveSrt = SrtCheck.IsChecked == true;
        s.FloatingCaptions = FloatingCheck.IsChecked == true;
        _ctx.Save();
        return new RecordingOptions(mode, monitor, s.Quality, s.RecordMicrophone, s.RecordSystemAudio, _ctx.RecorderMicId(), s.SubtitleLanguage,
            s.SubtitlesInEnglish, s.SaveSrt, s.OutputFolder, s.Glossary);
    }

    // ---- start and stop ----

    private async void Record_Click(object sender, RoutedEventArgs e) => await ToggleAsync();

    /// <summary>Start when idle, stop when recording. Also used by the global hotkey.</summary>
    public async Task ToggleAsync()
    {
        if (_busy) return;
        _busy = true;
        RecordButton.IsEnabled = false;
        try
        {
            if (_ctx.Session.IsRecording)
            {
                StateText.Text = Loc.T("rec.finishing");
                await _ctx.Session.StopAsync();
            }
            else await StartAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Recording action failed", ex);
            StatusText.Text = MeetWise.Audio.AudioErrors.Describe(ex);
            SetVisual(_ctx.Session.IsRecording);
        }
        finally { _busy = false; RecordButton.IsEnabled = true; }
    }

    private async Task StartAsync()
    {
        var options = BuildOptions();
        var status = new Progress<string>(t => StatusText.Text = t);
        StateText.Text = Loc.T("rec.preparing");
        bool whisper = options.SubtitleLanguage != "en" || options.TranslateToEnglish;
        if (whisper)
        {
            await _ctx.OfferGpuPackAsync(status);
            _ctx.Llm.Release(); // Whisper and the 8B language model do not fit in 8 GB together; the AI reloads when it is needed
        }

        _rows.Clear();
        _partials.Clear();
        _speaking.Clear();
        SavedBanner.Visibility = Visibility.Collapsed;
        await _ctx.Session.StartAsync(options);
        if (_ctx.Settings.FloatingCaptions) _captions ??= new CaptionOverlayWindow();
        SetVisual(true);
    }

    private void OnStopped(RecordingResult r)
    {
        _last = r;
        SetVisual(false);
        _captions?.Hide();
        _partials.Clear();
        var main = r.VideoPath ?? r.AudioPath ?? r.TxtPath;
        SavedText.Text = Loc.T("rec.saved", main) + (r.Notice is null ? "" : "\n" + r.Notice);
        PlayButton.Visibility = r.VideoPath is null ? Visibility.Collapsed : Visibility.Visible;
        SavedBanner.Visibility = Visibility.Visible;
        StatusText.Text = "";
        _ctx.NotifyItemsChanged();
    }

    // ---- live subtitles ----

    private void OnPartial(string channel, string text)
    {
        if (!_partials.TryGetValue(channel, out var row))
        {
            row = new SubtitleRow { Start = _ctx.Session.Elapsed.TotalSeconds, Speaker = channel + ":", IsPartial = true };
            _partials[channel] = row;
            _rows.Add(row);
        }
        row.Text = text;
        _captions?.SetText(text);
        LiveList.ScrollIntoView(row);
    }

    private void OnCue(SubtitleCue cue)
    {
        if (_partials.Remove(cue.Speaker, out var partial)) _rows.Remove(partial);
        var row = new SubtitleRow { Start = cue.Start, End = cue.End, Speaker = (cue.Speaker.Length > 0 ? cue.Speaker + ":" : ""), Text = cue.Text, Language = cue.Language };
        _rows.Add(row);
        _captions?.SetText(cue.Text);
        LiveList.ScrollIntoView(row);
    }

    private void OnSpeaking(string channel, bool on)
    {
        if (on) _speaking.Add(channel); else _speaking.Remove(channel);
        ListeningText.Visibility = _speaking.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- visuals ----

    private void SetVisual(bool recording)
    {
        RecordDot.CornerRadius = recording ? new CornerRadius(10) : new CornerRadius(34);
        RecordDot.Width = RecordDot.Height = recording ? 38 : 68;
        RecDotIndicator.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        StateText.Text = recording ? Loc.T("rec.stop") + " (Ctrl+Shift+R)" : Loc.T("rec.start") + " (Ctrl+Shift+R)";
        if (!recording) { MicLevel.Value = SystemLevel.Value = 0; }
        foreach (var c in new Control[] { MonitorCombo, MicCombo, UseMicCheck, SystemAudioCheck, ModeCombo, QualityCombo, LanguageCombo, EnglishCheck, SrtCheck })
            c.IsEnabled = !recording;
        if (!recording) Mode_Changed(null, null!);
    }

    private void Tick()
    {
        if (!_ctx.Session.IsRecording) return;
        TimerText.Text = _ctx.Session.Elapsed.ToString(@"hh\:mm\:ss");
        var (mic, sys) = _ctx.Session.Levels;
        MicLevel.Value = Math.Sqrt(mic);   // a gentle curve so quiet speech still moves the meter
        SystemLevel.Value = Math.Sqrt(sys);
        RecDotIndicator.Opacity = (_blink++ / 4) % 2 == 0 ? 1 : 0.25;
    }

    // ---- after a recording ----

    private void OpenSubtitles_Click(object sender, RoutedEventArgs e)
    {
        if (_last is not null) _ctx.OpenItem(_last.ItemId, "subtitles");
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_last?.VideoPath is { } v && File.Exists(v)) Process.Start(new ProcessStartInfo(v) { UseShellExecute = true });
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var target = _last?.VideoPath ?? _last?.AudioPath ?? _last?.TxtPath;
        if (target is not null && File.Exists(target)) Process.Start("explorer.exe", $"/select,\"{target}\"");
        else if (Directory.Exists(_ctx.Settings.OutputFolder)) Process.Start("explorer.exe", $"\"{_ctx.Settings.OutputFolder}\"");
    }
}
