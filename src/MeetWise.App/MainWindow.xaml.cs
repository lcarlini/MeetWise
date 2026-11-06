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
using MeetWise.Speech;
using MeetWise.Storage;

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
        _recorder.Start(_settings.OutputFolder, name, _settings.MicrophoneId);
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

        RecordButton.Content = "Stop recording";
        StatusText.Text = wantsText ? $"Recording and transcribing live → {_settings.OutputFolder}" : $"Recording → {_settings.OutputFolder}";
    }

    private void OnAudio(string source, NAudio.Wave.WaveFormat fmt, byte[] buffer, int count)
        => _live?.Push(source == "mic" ? "Me" : "Others", buffer, count);

    private void OnTranscript(TranscriptEvent e)
    {
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
        RecordButton.Content = "Start recording";
        StatusText.Text = $"Saved meeting #{_meetingId} ({_lines.Count} lines) in {Path.GetFullPath(_settings.OutputFolder)}";
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

    private void SaveSettings()
    {
        if (MicCombo.SelectedItem is AudioDeviceInfo m) _settings.MicrophoneId = m.Id;
        if (PresetCombo.SelectedItem is CapturePreset p) _settings.Preset = p;
        if (NoiseCombo.SelectedItem is NoiseMode n) _settings.NoiseCancellation = n;
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
        SaveSettings();
        base.OnClosing(e);
    }
}
