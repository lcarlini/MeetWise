using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeetWise.Audio;
using MeetWise.Core.Hardware;
using MeetWise.Core.Logging;
using MeetWise.Core.Settings;
using MeetWise.Intelligence;
using MeetWise.Speech;
using MeetWise.Storage;

namespace MeetWise.App;

/// <summary>The meeting tools: MeetWise Microphone, voice lock, English coach, live translation, notes, questions and action items.</summary>
public partial class MeetingsPage : UserControl
{
    private readonly StudioContext _ctx;
    private readonly ObservableCollection<TranscriptLine> _lines = new();
    private LiveTranslation? _translation;

    private AppSettings _settings => _ctx.Settings;
    private MeetingDatabase _db => _ctx.Db;
    private SettingsStore _store => _ctx.Store;
    private EnhancementController _enhance => _ctx.Enhance;
    private HttpClient _http => _ctx.Http;
    private HardwareProfile _hardware => _ctx.Hardware;
    private UserProfile Profile => _ctx.Profile;

    public MeetingsPage(StudioContext ctx)
    {
        InitializeComponent();
        _ctx = ctx;

        var mics = new AudioDeviceService().GetMicrophones();
        MicCombo.ItemsSource = mics;
        MicCombo.SelectedItem = mics.FirstOrDefault(m => m.Id == _settings.MicrophoneId) ?? mics[0];
        NoiseCombo.ItemsSource = Enum.GetValues<NoiseMode>();
        NoiseCombo.SelectedItem = _settings.NoiseCancellation;
        TranscriptList.ItemsSource = _lines;
        StatusText.Text = "Bot-free and local: your microphone and the system audio are transcribed on this PC.";

        InitMicrophoneCard();

        // live features that follow the recording
        var d = Dispatcher;
        _ctx.Session.Started += () => d.BeginInvoke(OnSessionStarted);
        _ctx.Session.Cue += c => d.BeginInvoke(() => OnCue(c));
        _ctx.Session.Stopped += _ => d.BeginInvoke(OnSessionStopped);
    }

    private Task<ILlmProvider> GetLlmAsync() => _ctx.GetLlmAsync(new Progress<string>(t => StatusText.Text = t));

    // ---- recording hooks ----

    private void OnSessionStarted()
    {
        SaveSettings();
        _translation?.Dispose();
        _translation = new LiveTranslation(_settings, GetLlmAsync, () => _ctx.Llm.TryGetInstalledLocalAsync(_ctx.Hardware),
            caption => _ctx.RaiseTranslation(caption), status => _ctx.ShowToast(status.Message));
        _ = StartCoachAsync();
    }

    private void OnCue(SubtitleCue cue)
    {
        if (cue.Speaker == "Me") _coach?.Submit(cue.Text, cue.Start, cue.End);
        _translation?.OnFinal(new MeetWise.Intelligence.LiveUtterance(cue.Speaker, cue.Text, cue.Language, cue.Start, cue.End));
    }

    private void OnSessionStopped()
    {
        _translation?.Dispose();
        _translation = null;
        var summary = StopCoach();
        if (summary is not null) StatusText.Text = summary;
    }

    // ---- meeting tools ----

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Search_Click(sender, e);
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        var hits = _db.Search(SearchBox.Text);
        _lines.Clear();
        foreach (var h in hits)
            _lines.Add(new TranscriptLine { Speaker = $"#{h.MeetingId} {h.Speaker}:", Time = $"[{TimeSpan.FromSeconds(h.StartSec):mm\\:ss}] ", Text = h.Text });
        StatusText.Text = $"{hits.Count} result(s) across all meetings.";
    }

    private async void Enhance_Click(object sender, RoutedEventArgs e)
    {
        EnhanceButton.IsEnabled = false;
        try
        {
            if (_enhance.IsRunning)
            {
                _enhance.Stop();
                EnhanceButton.Content = "Start cleanup";
                StatusText.Text = "Microphone cleanup stopped.";
            }
            else if (RenderCombo.SelectedItem is AudioDeviceInfo dev)
            {
                SaveSettings();
                var progress = new Progress<double>(p => StatusText.Text = $"Downloading enhancement model… {p:P0}");
                await _enhance.StartAsync(_settings, dev.Id, progress);
                EnhanceButton.Content = "Stop cleanup";
                StatusText.Text = $"Cleaning microphone → {dev.Name}";
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Enhancement failed", ex);
            StatusText.Text = "Enhancement failed: " + ex.Message;
        }
        finally { EnhanceButton.IsEnabled = true; }
    }

    /// <summary>Shows the pre-meeting brief of a calendar event.</summary>
    public async Task ShowBriefAsync(CalendarEvent evt)
    {
        try
        {
            var context = new PreMeetingAssistant(new NoLlm(), _db).BuildContext(evt);
            _lines.Clear();
            _lines.Add(new TranscriptLine { Speaker = evt.Title, Time = $"[{evt.StartUtc.ToLocalTime():HH:mm}] ", Text = context.Length == 0 ? "First meeting of this series." : "Writing your brief…" });
            if (context.Length == 0) return;
            var llm = await GetLlmAsync();
            var brief = await new PreMeetingAssistant(llm, _db).BriefAsync(evt, Profile);
            _lines.Clear();
            foreach (var line in brief.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                _lines.Add(new TranscriptLine { Speaker = "", Text = line.Trim() });
            StatusText.Text = $"Brief for \"{evt.Title}\".";
        }
        catch (Exception ex)
        {
            AppLog.Error("Brief failed", ex);
            StatusText.Text = "Brief failed: " + ex.Message;
        }
    }

    private sealed class NoLlm : ILlmProvider
    {
        public string Name => "none";
        public int ContextTokens => 0;
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, int maxTokens = 1024, CancellationToken ct = default)
            => throw new InvalidOperationException("No model is configured.");
    }

    private async void Notes_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var meeting = _db.ListMeetings(limit: 1).FirstOrDefault();
            if (meeting is null) { StatusText.Text = "No meetings yet."; return; }
            var segments = _db.GetTranscript(meeting.Id);
            if (segments.Count == 0) { StatusText.Text = "The latest meeting has no transcript."; return; }
            StatusText.Text = "Preparing the AI model…";
            var llm = await GetLlmAsync();
            StatusText.Text = $"Writing notes with {llm.Name}…";
            var notes = await new MeetingIntelligence(llm).GenerateNotesAsync(
                MeetingIntelligence.FormatTranscript(segments), _settings.TemplateId, Profile, meeting.StartedUtc);
            MeetingIntelligence.Save(_db, meeting.Id, notes);

            _lines.Clear();
            void Add(string head, IEnumerable<string> items)
            {
                foreach (var i in items) _lines.Add(new TranscriptLine { Speaker = head, Text = i });
            }
            _lines.Add(new TranscriptLine { Speaker = "Summary:", Text = notes.Summary });
            Add("Key point:", notes.KeyPoints);
            Add("Decision:", notes.Decisions);
            Add("Risk:", notes.Risks);
            Add("Action:", notes.ActionItems.Select(a => $"{a.Text}{(a.Owner is null ? "" : " — " + a.Owner)}{(a.DueUtc is null ? "" : " (due " + a.DueUtc.Value.ToString("yyyy-MM-dd") + ")")}"));
            StatusText.Text = $"Notes for \"{meeting.Title}\" saved; {notes.ActionItems.Count} action item(s) added.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Notes failed", ex);
            StatusText.Text = "Notes failed: " + ex.Message;
        }
    }

    private async void Ask_Click(object sender, RoutedEventArgs e)
    {
        var question = SearchBox.Text.Trim();
        if (question.Length == 0) { StatusText.Text = "Type a question in the box first."; return; }
        try
        {
            StatusText.Text = "Preparing the AI model…";
            var llm = await GetLlmAsync();
            StatusText.Text = "Thinking…";
            var answer = await new AskMeetWise(llm, _db).AskAsync(question, null, Profile);
            _lines.Clear();
            _lines.Add(new TranscriptLine { Speaker = "Answer:", Text = answer.Text });
            foreach (var s in answer.Sources)
                _lines.Add(new TranscriptLine { Speaker = $"#{s.MeetingId} {s.Speaker}:", Time = $"[{TimeSpan.FromSeconds(s.StartSec):mm\\:ss}] ", Text = s.Text });
            StatusText.Text = $"Answered from {answer.Sources.Count} transcript passage(s).";
        }
        catch (Exception ex)
        {
            AppLog.Error("Ask failed", ex);
            StatusText.Text = "Ask failed: " + ex.Message;
        }
    }

    private async void Actions_Click(object sender, RoutedEventArgs e)
    {
        var items = _db.OpenActionItems();
        _lines.Clear();
        foreach (var a in items)
            _lines.Add(new TranscriptLine
            {
                Speaker = $"#{a.MeetingId}",
                Time = a.DueUtc is { } d ? $"[{d:yyyy-MM-dd}] " : "",
                Text = a.Text + (a.Owner is null ? "" : " — " + a.Owner)
            });
        StatusText.Text = $"{items.Count} open action item(s).";
        if (items.Count == 0 || string.IsNullOrWhiteSpace(_settings.WebhookUrl)) return;
        if (MessageBox.Show($"Send {items.Count} action item(s) to your webhook?", "MeetWise", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            var latest = _db.ListMeetings(limit: 1).First();
            await new WebhookExporter(_http).ExportAsync(_settings.WebhookUrl, latest, items);
            StatusText.Text = "Action items sent to the webhook.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Webhook failed", ex);
            StatusText.Text = "Webhook failed: " + ex.Message;
        }
    }

    public void SaveSettings()
    {
        if (MicCombo.SelectedItem is AudioDeviceInfo m) _settings.MicrophoneId = m.Id;
        if (NoiseCombo.SelectedItem is NoiseMode n) _settings.NoiseCancellation = n;
        _settings.EchoCancellation = EchoCheck.IsChecked == true;
        _settings.VoiceIsolation = IsolateCheck.IsChecked == true;
        CaptureMicSettings();
        _store.Save(_settings);
    }

    public void Shutdown()
    {
        _micTimer.Stop();
        StopCoach();
        _overlay?.Close();
        _translation?.Dispose();
    }
}
