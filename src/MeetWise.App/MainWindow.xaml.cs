using System.ComponentModel;
using System.IO;
using System.Windows;
using MeetWise.Audio;
using MeetWise.Core.Hardware;
using MeetWise.Core.Logging;
using MeetWise.Core.Settings;
using MeetWise.Storage;

namespace MeetWise.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private readonly AppSettings _settings;
    private readonly DualChannelRecorder _recorder = new();
    private readonly MeetingDatabase _db = new(MeetWise.Core.AppPaths.Database);
    private long _meetingId;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _store.Load();

        var hw = HardwareDetector.Detect(_settings.TierOverride);
        var gpu = hw.Gpus.Count > 0 ? $"{hw.Gpus[0].Name} ({hw.Gpus[0].VramMb / 1024.0:0.#} GB)" : "no dedicated GPU";
        HardwareText.Text = $"{gpu} · {hw.CpuThreads} threads · tier {hw.Tier} · local models: Whisper {hw.WhisperModel}";

        var mics = new AudioDeviceService().GetMicrophones();
        MicCombo.ItemsSource = mics;
        MicCombo.SelectedItem = mics.FirstOrDefault(m => m.Id == _settings.MicrophoneId) ?? mics[0];
        PresetCombo.ItemsSource = Enum.GetValues<CapturePreset>();
        PresetCombo.SelectedItem = _settings.Preset;
        NoiseCombo.ItemsSource = Enum.GetValues<NoiseMode>();
        NoiseCombo.SelectedItem = _settings.NoiseCancellation;
        StatusText.Text = "Bot-free: records your microphone and the system audio locally.";
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_recorder.IsRecording) StopRecording(); else StartRecording();
        }
        catch (Exception ex)
        {
            AppLog.Error("Recording action failed", ex);
            StatusText.Text = "Error: " + ex.Message;
        }
    }

    private void StartRecording()
    {
        SaveSettings();
        var name = $"meeting-{DateTime.Now:yyyyMMdd-HHmmss}";
        _recorder.Start(_settings.OutputFolder, name, _settings.MicrophoneId);
        _meetingId = _db.AddMeeting(name, DateTime.UtcNow, audioPath: _recorder.MicPath);
        RecordButton.Content = "Stop recording";
        StatusText.Text = $"Recording to {_settings.OutputFolder}";
    }

    private void StopRecording()
    {
        _recorder.Stop();
        RecordButton.Content = "Start recording";
        StatusText.Text = $"Saved meeting #{_meetingId} in {Path.GetFullPath(_settings.OutputFolder)}";
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
