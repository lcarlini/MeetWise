using System.IO;
using System.Net.Http;
using System.Windows;
using MeetWise.Audio;
using MeetWise.Core;
using MeetWise.Core.Logging;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;
using MeetWise.Speech;
using NAudio.CoreAudioApi;

namespace MeetWise.App;

/// <summary>
/// Guided voice training. Each sentence is checked (enough speech, not clipped, loud enough, quiet room, same person) before it is accepted;
/// the profile is built from the accepted sentences, and a test panel lets the user hear the original against the voice-locked result.
/// </summary>
public partial class VoiceProfileWindow : Window
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly List<float[]> _accepted = new();
    private readonly List<float[]> _embeddings = new();
    private SpeakerEmbedder? _embedder;
    private string _vadModel = "";
    private string _embeddingModel = "";
    private ClipRecorder? _recorder;
    private int _index;
    private float[]? _testRaw, _testLocked;
    private int _testRate = 16000;

    /// <summary>True when the window created or deleted a profile, so the caller can refresh its controls.</summary>
    public bool Changed { get; private set; }

    public VoiceProfileWindow(AppSettings settings, HttpClient http)
    {
        InitializeComponent();
        _settings = settings;
        _http = http;
        Loaded += async (_, _) => await PrepareAsync();
    }

    private async Task PrepareAsync()
    {
        try
        {
            var manager = new ModelManager(_http);
            ResultText.Text = "Preparing the voice models (one time)…";
            await manager.EnsureAsync(ModelCatalog.SileroVad);
            await manager.EnsureAsync(ModelCatalog.SpeakerEmbedding);
            _vadModel = manager.PathFor(ModelCatalog.SileroVad);
            _embeddingModel = manager.PathFor(ModelCatalog.SpeakerEmbedding);
            _embedder = await Task.Run(() => new SpeakerEmbedder(_embeddingModel));

            if (VoiceProfile.IsTrained())
            {
                StepText.Text = "You already have a voice profile.";
                PromptText.Text = "You can test it below, delete it, or train again by pressing Start recording.";
                ShowTest();
                DeleteButton.Visibility = Visibility.Visible;
            }
            else ShowPrompt();
            RecordButton.IsEnabled = true;
            ResultText.Text = "";
        }
        catch (Exception ex)
        {
            AppLog.Error("Voice training could not start", ex);
            ResultText.Text = "Could not prepare the voice models: " + ex.Message;
        }
    }

    private void ShowPrompt()
    {
        StepText.Text = Loc.T("voice.step", _index + 1, VoiceTrainer.Prompts.Length);
        PromptText.Text = VoiceTrainer.Prompts[_index];
    }

    private void ShowTest() => TestPanel.Visibility = Visibility.Visible;

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder is null)
        {
            if (VoiceProfile.IsTrained() && _accepted.Count == 0 && _index == 0 && DeleteButton.Visibility == Visibility.Visible)
            {
                // training again from scratch: the old profile is replaced only when the new one is complete
                DeleteButton.Visibility = Visibility.Collapsed;
                TestPanel.Visibility = Visibility.Collapsed;
                ShowPrompt();
            }
            try
            {
                _recorder = new ClipRecorder(_settings.MicrophoneId, _settings.PhysicalMicId);
                _recorder.Level += peak => Dispatcher.BeginInvoke(() => LevelBar.Value = peak);
                _recorder.Start();
                RecordButton.Content = Loc.T("voice.stop");
                ResultText.Text = "Listening… read the sentence, then press Stop.";
            }
            catch (Exception ex)
            {
                _recorder?.Dispose();
                _recorder = null;
                ResultText.Text = "Could not open the microphone: " + ex.Message;
            }
            return;
        }

        var samples = _recorder.Stop();
        _recorder.Dispose();
        _recorder = null;
        LevelBar.Value = 0;
        RecordButton.IsEnabled = false;
        RecordButton.Content = Loc.T("voice.start");
        ResultText.Text = "Checking the recording…";
        try { await AcceptOrRejectAsync(samples); }
        finally { RecordButton.IsEnabled = true; }
    }

    private async Task AcceptOrRejectAsync(float[] samples)
    {
        var quality = await Task.Run(() => VoiceTrainer.Analyze(samples, _vadModel));
        if (!quality.Ok) { ResultText.Text = quality.Message + " Press Start recording to try again."; return; }

        var embedding = await Task.Run(() => _embedder!.Embed(samples));
        var inconsistent = VoiceTrainer.CheckConsistency(embedding, _embeddings);
        if (inconsistent is not null) { ResultText.Text = inconsistent + " Press Start recording to try again."; return; }

        _accepted.Add(samples);
        _embeddings.Add(embedding);
        _index++;
        if (_index < VoiceTrainer.Prompts.Length)
        {
            ShowPrompt();
            ResultText.Text = $"Good ({quality.SpeechSeconds:0.0} s of clear speech). Next sentence.";
            return;
        }
        await BuildProfileAsync();
    }

    private async Task BuildProfileAsync()
    {
        ResultText.Text = "Building your voice profile…";
        RecordButton.IsEnabled = false;
        try
        {
            var profile = await Task.Run(() => VoiceTrainer.Build(_accepted, _vadModel, _embedder!, "Me"));
            _settings.VoiceIsolation = true;
            Changed = true;
            StepText.Text = "Your voice profile is ready.";
            PromptText.Text = "MeetWise will now pass only your voice. Test it below, then close this window.";
            ResultText.Text = $"Saved from {profile.Sentences} sentences.";
            DeleteButton.Visibility = Visibility.Visible;
            ShowTest();
        }
        catch (Exception ex)
        {
            AppLog.Error("Building the voice profile failed", ex);
            ResultText.Text = "Could not build the profile: " + ex.Message;
        }
        finally
        {
            _accepted.Clear();
            _embeddings.Clear();
            _index = 0;
            RecordButton.IsEnabled = true;
        }
    }

    // ---- test: original versus voice-locked ----

    private async void TestRecord_Click(object sender, RoutedEventArgs e)
    {
        TestRecordButton.IsEnabled = false;
        PlayRawButton.IsEnabled = PlayLockedButton.IsEnabled = false;
        try
        {
            var manager = new ModelManager(_http);
            TestResultText.Text = "Preparing voice isolation (one time)…";
            await manager.EnsureAsync(ModelCatalog.VoiceIsolationFor());

            using var rec = new ClipRecorder(_settings.MicrophoneId, _settings.PhysicalMicId);
            rec.Level += peak => Dispatcher.BeginInvoke(() => LevelBar.Value = peak);
            rec.Start();
            for (int s = 10; s > 0; s--)
            {
                TestResultText.Text = $"Recording… talk now, and let someone else talk too ({s} s)";
                await Task.Delay(1000);
            }
            _testRaw = rec.Stop();
            LevelBar.Value = 0;

            TestResultText.Text = "Applying your voice lock…";
            var voxDir = manager.PathFor(ModelCatalog.VoiceIsolationFor());
            var raw = _testRaw;
            _testLocked = await Task.Run(() =>
            {
                using var engine = new EnhancementEngine(16000, new EnhancementOptions(EnhancerKind.VoiceIsolation, null, false, voxDir, VoiceProfile.ClipPath(), Math.Clamp(Environment.ProcessorCount / 4, 2, 4), MinBlockMs: 80));
                var o = new List<float>();
                for (int i = 0; i < raw.Length; i += 160) o.AddRange(engine.Process(raw.AsSpan(i, Math.Min(160, raw.Length - i)).ToArray()));
                _testRate = engine.OutputSampleRate;
                return o.ToArray();
            });

            double Rms(float[] x) => Math.Sqrt(x.Length == 0 ? 0 : x.Sum(v => (double)v * v) / x.Length);
            double change = 20 * Math.Log10(Math.Max(Rms(_testLocked), 1e-9) / Math.Max(Rms(_testRaw), 1e-9));
            TestResultText.Text = $"Done. Overall level changed by {change:0.0} dB (more negative means more of the other sound was removed). Play both to compare.";
            PlayRawButton.IsEnabled = PlayLockedButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Voice lock test failed", ex);
            TestResultText.Text = "The test failed: " + ex.Message;
        }
        finally { TestRecordButton.IsEnabled = true; }
    }

    private async void PlayRaw_Click(object sender, RoutedEventArgs e) => await PlayAsync(_testRaw, 16000);
    private async void PlayLocked_Click(object sender, RoutedEventArgs e) => await PlayAsync(_testLocked, _testRate);

    private async Task PlayAsync(float[]? samples, int rate)
    {
        if (samples is null) return;
        try
        {
            using var en = new MMDeviceEnumerator();
            using var device = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var player = new PcmPlayer(device, rate);
            player.Enqueue(samples);
            await Task.Delay(TimeSpan.FromSeconds(samples.Length / (double)rate + 0.5));
        }
        catch (Exception ex) { TestResultText.Text = "Could not play the recording: " + ex.Message; }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Delete your voice profile? Voice isolation will be turned off.", "MeetWise", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        VoiceProfile.Delete();
        _settings.VoiceIsolation = false;
        Changed = true;
        DeleteButton.Visibility = Visibility.Collapsed;
        TestPanel.Visibility = Visibility.Collapsed;
        StepText.Text = "Your voice profile was deleted.";
        PromptText.Text = "Press Start recording to train again.";
        _index = 0;
        ShowPrompt();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _recorder?.Dispose();
        _embedder?.Dispose();
        base.OnClosed(e);
    }
}
