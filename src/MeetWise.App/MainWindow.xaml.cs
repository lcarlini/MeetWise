using System.Net.Http;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MeetWise.Audio;
using MeetWise.Core;
using MeetWise.Core.Hardware;
using MeetWise.Core.Logging;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;
using MeetWise.Intelligence;
using MeetWise.Speech;
using MeetWise.Storage;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;

namespace MeetWise.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private readonly AppSettings _settings;
    private readonly HardwareProfile _hardware;
    private readonly DualChannelRecorder _recorder = new();
    private readonly MeetingDatabase _db = new(AppPaths.Database);
    private readonly ObservableCollection<TranscriptLine> _lines = new();
    private readonly Dictionary<string, TranscriptLine> _openPartials = new();
    private readonly HttpClient _http = new();
    private readonly EnhancementController _enhance;
    private readonly SecretStore _secrets = new();
    private readonly LlmFactory _llmFactory;
    private Glossary _glossary;
    private LiveTranslation? _translation;
    private VideoRecorder? _video;
    private string? _ffmpegPath;
    private string? _baseName;
    private Task? _finishing;
    private readonly TaskbarIcon _tray;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly CalendarService _calendar;
    private readonly MeetingDetector _detector = new();
    private ReminderScheduler _reminders;
    private Func<Task>? _balloonAction;
    private LiveTranscriber? _live;
    private long _meetingId;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _store.Load();
        _hardware = HardwareDetector.Detect(_settings.TierOverride);
        var gpu = _hardware.Gpus.Count > 0 ? $"{_hardware.Gpus[0].Name} ({_hardware.Gpus[0].VramMb / 1024.0:0.#} GB)" : "no dedicated GPU";
        HardwareText.Text = $"{gpu} · {_hardware.CpuThreads} threads · tier {_hardware.Tier}";

        var mics = new AudioDeviceService().GetMicrophones();
        MicCombo.ItemsSource = mics;
        MicCombo.SelectedItem = mics.FirstOrDefault(m => m.Id == _settings.MicrophoneId) ?? mics[0];
        PresetCombo.ItemsSource = Enum.GetValues<CapturePreset>();
        PresetCombo.SelectedItem = _settings.Preset;
        NoiseCombo.ItemsSource = Enum.GetValues<NoiseMode>();
        NoiseCombo.SelectedItem = _settings.NoiseCancellation;
        TranscriptList.ItemsSource = _lines;
        _enhance = new EnhancementController(_http);
        _llmFactory = new LlmFactory(_http, _secrets);
        _glossary = new Glossary(_settings.Glossary);
        _calendar = new CalendarService(_http);
        _reminders = new ReminderScheduler(TimeSpan.FromMinutes(_settings.ReminderMinutes));
        _tray = new TaskbarIcon { Icon = System.Drawing.SystemIcons.Application, ToolTipText = "MeetWise" };
        _tray.TrayMouseDoubleClick += (_, _) => RestoreFromTray();
        _tray.TrayBalloonTipClicked += async (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            RestoreFromTray();
            if (action is not null) await action();
        };
        _tick.Tick += async (_, _) => await TickAsync();
        _tick.Start();
        _ = RecoverUnfinishedVideosAsync();
        var outs = new AudioDeviceService().GetRenderDevices().OrderByDescending(d => AudioDeviceService.LooksVirtual(d.Name)).ToList();
        RenderCombo.ItemsSource = outs;
        RenderCombo.SelectedIndex = outs.Count > 0 ? 0 : -1;
        VirtualHint.Visibility = outs.Any(d => AudioDeviceService.LooksVirtual(d.Name)) ? Visibility.Collapsed : Visibility.Visible;
        EchoCheck.IsChecked = _settings.EchoCancellation;
        IsolateCheck.IsChecked = _settings.VoiceIsolation;
        StatusText.Text = "Bot-free and local: your microphone and the system audio are transcribed on this PC.";
    }

    private async Task<string> EnsureAsrModelAsync()
    {
        var model = ModelCatalog.StreamingEnglish;
        var manager = new ModelManager(_http);
        if (!manager.IsInstalled(model))
        {
            var progress = new Progress<double>(p => StatusText.Text = $"Downloading speech model… {p:P0} (one time, ~300 MB)");
            await manager.EnsureAsync(model, progress);
        }
        return manager.PathFor(model);
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        RecordButton.IsEnabled = false;
        try
        {
            if (_recorder.IsRecording) StopRecording(); else await StartRecordingAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Recording action failed", ex);
            StatusText.Text = "Error: " + ex.Message;
        }
        finally { RecordButton.IsEnabled = true; }
    }

    private async Task StartRecordingAsync()
    {
        SaveSettings();
        var wantsText = _settings.Preset != CapturePreset.AudioOnly;
        string? modelDir = wantsText ? await EnsureAsrModelAsync() : null;

        var name = $"meeting-{DateTime.Now:yyyyMMdd-HHmmss}";
        _baseName = name;
        if (_settings.Preset == CapturePreset.AudioVideo)
        {
            _ffmpegPath = await FFmpegPack.EnsureAsync(_http, new Progress<double>(p => StatusText.Text = $"Downloading FFmpeg… {p:P0} (one time)"));
            var encoders = await VideoRecorder.ProbeEncodersAsync(_ffmpegPath);
            Directory.CreateDirectory(_settings.OutputFolder);
            _video = new VideoRecorder(_ffmpegPath);
            _video.Start(Path.Combine(_settings.OutputFolder, name + ".video.mkv"), encoders[0]);
        }
        try
        {
            _recorder.Start(_settings.OutputFolder, name, _settings.MicrophoneId);
        }
        catch
        {
            _video?.Dispose(); // do not leave a screen recording running when the audio devices failed
            _video = null;
            throw;
        }
        _meetingId = _db.AddMeeting(name, DateTime.UtcNow, audioPath: _recorder.MicPath);
        _lines.Clear();
        _openPartials.Clear();

        if (modelDir is not null)
        {
            _live = new LiveTranscriber(modelDir);
            var threads = Math.Max(2, _hardware.CpuThreads / 4);
            if (_recorder.MicFormat is { } mf)
                _live.AddChannel("Me", mf.SampleRate, mf.Channels, mf.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat, threads);
            if (_recorder.LoopbackFormat is { } lf)
                _live.AddChannel("Others", lf.SampleRate, lf.Channels, lf.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat, threads);
            _live.Transcript += e => Dispatcher.BeginInvoke(() => OnTranscript(e));
            _recorder.DataAvailable += OnAudio;
        }

        _translation?.Dispose();
        _translation = new LiveTranslation(_settings, GetLlmAsync, (who, original, translated) => Dispatcher.BeginInvoke(() =>
            _lines.Add(new TranscriptLine { Speaker = "→", Text = translated })));
        RecordButton.Content = "Stop recording";
        StatusText.Text = wantsText ? $"Recording and transcribing live → {_settings.OutputFolder}" : $"Recording → {_settings.OutputFolder}";
    }

    private void OnAudio(string source, NAudio.Wave.WaveFormat fmt, byte[] buffer, int count)
        => _live?.Push(source == "mic" ? "Me" : "Others", buffer, count);

    private void OnTranscript(TranscriptEvent e)
    {
        if (e.IsFinal) e = e with { Text = _glossary.Apply(e.Text) };
        if (!_openPartials.TryGetValue(e.Channel, out var line))
        {
            line = new TranscriptLine { Speaker = e.Channel + ":", Time = $"[{TimeSpan.FromSeconds(e.StartSec):mm\\:ss}] " };
            _openPartials[e.Channel] = line;
            _lines.Add(line);
        }
        line.Text = e.Text;
        if (e.IsFinal)
        {
            _openPartials.Remove(e.Channel);
            _translation?.OnFinal(e);
            if (_meetingId != 0) _db.AddSegment(_meetingId, e.Channel, e.StartSec, e.EndSec, e.Text);
        }
        TranscriptList.ScrollIntoView(_lines[^1]);
    }

    private void StopRecording()
    {
        _recorder.DataAvailable -= OnAudio;
        _recorder.Stop();
        var live = _live;
        _live = null;
        live?.Dispose();
        _translation?.Dispose();
        _translation = null;
        RecordButton.Content = "Start recording";
        StatusText.Text = $"Saved meeting #{_meetingId} ({_lines.Count} lines) in {Path.GetFullPath(_settings.OutputFolder)}";
        if (_recorder.LoopbackPath is { } loop && _settings.Preset != CapturePreset.AudioOnly)
            _ = LabelSpeakersAsync(_meetingId, loop);
        if (_video is not null) _finishing = FinishVideoAsync(_video, _ffmpegPath!, _baseName!, _recorder.MicPath, _recorder.LoopbackPath, _recorder.StartedUtc);
        _video = null;
    }

    /// <summary>After a crash or power loss the MKV capture is still on disk: turn any orphaned one into an MP4 the next time the app starts.</summary>
    private async Task RecoverUnfinishedVideosAsync()
    {
        try
        {
            var ffmpeg = FFmpegPack.Find();
            if (ffmpeg is null || !Directory.Exists(_settings.OutputFolder)) return;
            foreach (var mkv in Directory.GetFiles(_settings.OutputFolder, "*.video.mkv"))
            {
                var baseName = Path.GetFileName(mkv)[..^".video.mkv".Length];
                var mp4 = Path.Combine(_settings.OutputFolder, baseName + ".mp4");
                if (File.Exists(mp4) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(mkv)).TotalSeconds < 30) continue; // finished, or still being written
                var mic = Path.Combine(_settings.OutputFolder, baseName + ".mic.wav");
                var loop = Path.Combine(_settings.OutputFolder, baseName + ".loop.wav");
                await VideoRecorder.MuxAsync(ffmpeg, mkv, File.Exists(mic) ? mic : null, File.Exists(loop) ? loop : null,
                    File.GetCreationTimeUtc(mkv), File.Exists(mic) ? File.GetCreationTimeUtc(mic) : File.GetCreationTimeUtc(mkv), mp4);
                AppLog.Info("Recovered unfinished video: " + mp4);
            }
        }
        catch (Exception ex) { AppLog.Warn("Video recovery skipped: " + ex.Message); }
    }

    /// <summary>Stops the screen capture and joins it with both audio channels into one MP4 (the WAV files are kept for Refine).</summary>
    private async Task FinishVideoAsync(VideoRecorder video, string ffmpeg, string baseName, string? mic, string? loop, DateTime audioStartUtc)
    {
        try
        {
            await video.StopAsync();
            var mp4 = Path.Combine(_settings.OutputFolder, baseName + ".mp4");
            await VideoRecorder.MuxAsync(ffmpeg, video.OutputPath!, mic, loop, video.StartedUtc, audioStartUtc, mp4);
            _ = Dispatcher.BeginInvoke(() => StatusText.Text = $"Video saved: {mp4}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Video finalize failed", ex);
            _ = Dispatcher.BeginInvoke(() => StatusText.Text = $"Video could not be finalized ({ex.Message}). The raw capture is kept at {video.OutputPath}.");
        }
        finally { video.Dispose(); }
    }

    /// <summary>Background pass after the meeting: split "Others" into Speaker 1, Speaker 2... and use enrolled names when voices match.</summary>
    private async Task LabelSpeakersAsync(long meetingId, string loopbackWav)
    {
        try
        {
            var manager = new ModelManager(_http);
            await manager.EnsureAsync(ModelCatalog.DiarizationSegmentation);
            await manager.EnsureAsync(ModelCatalog.SpeakerEmbedding);
            int changed = await Task.Run(() =>
            {
                var audio = AudioFile.ReadMono16k(loopbackWav);
                using var diarizer = new Diarizer(
                    Path.Combine(manager.PathFor(ModelCatalog.DiarizationSegmentation), "model.onnx"),
                    manager.PathFor(ModelCatalog.SpeakerEmbedding));
                using var profiles = new SpeakerProfiles(manager.PathFor(ModelCatalog.SpeakerEmbedding),
                    Path.Combine(AppPaths.Roaming, "voices", "voices.json"));
                return SpeakerLabeling.Apply(_db, meetingId, audio, diarizer, profiles);
            });
            _ = Dispatcher.BeginInvoke(() => StatusText.Text = $"Meeting #{meetingId}: identified speakers in {changed} segment(s).");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Speaker labeling skipped: " + ex.Message);
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import audio or video",
            Filter = "Media|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.mp4;*.mkv;*.mov;*.webm;*.avi|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var modelDir = await EnsureAsrModelAsync();
            var progress = new Progress<double>(p => StatusText.Text = $"Transcribing {Path.GetFileName(dlg.FileName)}… {p:P0}");
            var events = await Task.Run(() => FileTranscriber.Transcribe(dlg.FileName, modelDir, "Speaker", progress));
            var id = _db.AddMeeting(Path.GetFileNameWithoutExtension(dlg.FileName), DateTime.UtcNow, audioPath: dlg.FileName);
            _lines.Clear();
            foreach (var ev in events)
            {
                _db.AddSegment(id, ev.Channel, ev.StartSec, ev.EndSec, ev.Text);
                _lines.Add(new TranscriptLine { Speaker = ev.Channel + ":", Time = $"[{TimeSpan.FromSeconds(ev.StartSec):mm\\:ss}] ", Text = ev.Text });
            }
            StatusText.Text = $"Imported as meeting #{id}: {events.Count} segments.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Import failed", ex);
            StatusText.Text = "Import failed: " + ex.Message;
        }
    }

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

    private async void Enroll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettings();
            StatusText.Text = "Speak naturally for 10 seconds (read anything aloud)…";
            await VoiceEnrollment.RecordAsync(_settings.MicrophoneId, VoiceEnrollment.DefaultPath, TimeSpan.FromSeconds(10));
            IsolateCheck.IsChecked = true;
            StatusText.Text = "Voice enrolled. Voice isolation is on for the next cleanup session.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Enrollment failed", ex);
            StatusText.Text = "Enrollment failed: " + ex.Message;
        }
    }

    /// <summary>Second pass with Whisper (GPU when the acceleration pack is installed), then speaker labeling again.</summary>
    private async void Refine_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var meeting = _db.ListMeetings(limit: 1).FirstOrDefault();
            if (meeting?.AudioPath is not { } mic || !File.Exists(mic)) { StatusText.Text = "The latest meeting has no recorded audio to refine."; return; }
            var loop = mic.EndsWith(".mic.wav", StringComparison.OrdinalIgnoreCase) ? mic[..^8] + ".loop.wav" : null;

            _llmFactory.Release(); // free VRAM for Whisper
            var manager = new ModelManager(_http);
            var model = ModelCatalog.WhisperFor(_hardware);
            await OfferGpuPackAsync();
            CudaRuntimePack.Activate();
            await manager.EnsureAsync(model, new Progress<double>(p => StatusText.Text = $"Downloading {model.Id}… {p:P0}"));

            var progress = new Progress<double>(p => StatusText.Text = $"Refining \"{meeting.Title}\"… {p:P0}");
            int count = await Task.Run(async () =>
            {
                using var whisper = new WhisperTranscriber(manager.PathFor(model));
                return await MeetingRefiner.RefineAsync(_db, meeting.Id, mic, loop, whisper, _settings.SpokenLanguage, _settings.Glossary, progress);
            });

            var segments = _db.GetTranscript(meeting.Id);
            _lines.Clear();
            foreach (var s in segments)
                _lines.Add(new TranscriptLine { Speaker = s.Speaker + ":", Time = $"[{TimeSpan.FromSeconds(s.StartSec):mm\\:ss}] ", Text = _glossary.Apply(s.Text) });
            StatusText.Text = $"Refined: {count} segment(s) with {model.Id}.";
            if (loop is not null && File.Exists(loop)) _ = LabelSpeakersAsync(meeting.Id, loop);
        }
        catch (Exception ex)
        {
            AppLog.Error("Refine failed", ex);
            StatusText.Text = "Refine failed: " + ex.Message;
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray) Hide();
    }

    private void Notify(string title, string text, Func<Task>? onClick)
    {
        _balloonAction = onClick;
        _tray.ShowBalloonTip(title, text, BalloonIcon.Info);
    }

    /// <summary>Every 30 s: offer to record when a conferencing app grabs the mic, and announce upcoming calendar meetings.</summary>
    private async Task TickAsync()
    {
        try
        {
            if (_settings.AutoDetectMeetings && !_recorder.IsRecording && _detector.Poll() is { } call)
                Notify("MeetWise", $"{call.Process} is using your microphone. Click to start recording.",
                    async () => { if (!_recorder.IsRecording) await StartRecordingAsync(); });

            if (!string.IsNullOrWhiteSpace(_settings.CalendarUrl))
            {
                var upcoming = await _calendar.UpcomingAsync(_settings.CalendarUrl, DateTime.UtcNow, TimeSpan.FromHours(12));
                foreach (var r in _reminders.Due(upcoming, DateTime.UtcNow))
                {
                    var evt = r.Event;
                    var mins = Math.Max(0, (int)Math.Round(r.Remaining.TotalMinutes));
                    Notify($"Starting in {mins} min", $"{evt.Title}. Click for your brief.", () => ShowBriefAsync(evt));
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Background check failed: " + ex.Message);
        }
    }

    private async Task ShowBriefAsync(CalendarEvent evt)
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

    private UserProfile Profile => new(_settings.UserNickname, _settings.UserOccupation, _settings.UserContext);

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        var dlg = new SettingsWindow(_settings, _secrets) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _store.Save(_settings);
            _glossary = new Glossary(_settings.Glossary);
            _reminders = new ReminderScheduler(TimeSpan.FromMinutes(_settings.ReminderMinutes));
            StatusText.Text = "Settings saved.";
        }
    }

    private bool _gpuPackAsked;

    /// <summary>On NVIDIA cards, offers the 400 MB GPU acceleration pack once per session. Declining keeps everything working on the CPU.</summary>
    private async Task OfferGpuPackAsync()
    {
        if (_hardware.Tier != HardwareTier.A_NvidiaRtx || CudaRuntimePack.IsInstalled() || _gpuPackAsked) return;
        _gpuPackAsked = true;
        var ask = MessageBox.Show("Install the GPU acceleration pack (about 400 MB from NVIDIA)? Transcription refinement and the local AI run several times faster on your graphics card. Choose No to use the CPU.",
            "MeetWise", MessageBoxButton.YesNo);
        if (ask == MessageBoxResult.Yes)
            await CudaRuntimePack.EnsureAsync(_http, new Progress<double>(p => StatusText.Text = $"Downloading GPU acceleration pack… {p:P0}"));
    }

    private async Task<ILlmProvider> GetLlmAsync()
    {
        if (_settings.CloudProvider is null) await OfferGpuPackAsync();
        var progress = new Progress<double>(p => StatusText.Text = $"Downloading the local AI model… {p:P0} (one time)");
        return await _llmFactory.GetAsync(_settings, _hardware, progress);
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

    private void SaveSettings()
    {
        if (MicCombo.SelectedItem is AudioDeviceInfo m) _settings.MicrophoneId = m.Id;
        if (PresetCombo.SelectedItem is CapturePreset p) _settings.Preset = p;
        if (NoiseCombo.SelectedItem is NoiseMode n) _settings.NoiseCancellation = n;
        _settings.EchoCancellation = EchoCheck.IsChecked == true;
        _settings.VoiceIsolation = IsolateCheck.IsChecked == true;
        _store.Save(_settings);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_recorder.IsRecording)
        {
            var r = MessageBox.Show("A recording is in progress. Stop and exit?", "MeetWise", MessageBoxButton.YesNo);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
            StopRecording();
        }
        if (_finishing is { IsCompleted: false }) _finishing.Wait(TimeSpan.FromMinutes(2)); // let the MP4 finish before exiting
        _tick.Stop();
        _tray.Dispose();
        _enhance.Dispose();
        SaveSettings();
        base.OnClosing(e);
    }
}
