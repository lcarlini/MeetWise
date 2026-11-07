using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;
using MeetWise.Audio;
using MeetWise.Core.Logging;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;
using MeetWise.Intelligence;
using MeetWise.Speech;

namespace MeetWise.App;

/// <summary>The MeetWise Microphone card (virtual mic, voice lock, always-on, start with Windows) and the English coach.</summary>
public partial class MeetingsPage
{
    private readonly DispatcherTimer _micTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    public event Action<string>? TrayTipChanged;
    private readonly StartupRegistration _startup = new();
    private CoachSession? _coach;
    private EnglishPunctuator? _punct;
    private CoachOverlayWindow? _overlay;
    private readonly List<CoachFeedback> _coachLog = new();

    private void InitMicrophoneCard()
    {
        AlwaysOnCheck.IsChecked = _settings.AlwaysOnMicrophone;
        StartupCheck.IsChecked = _startup.IsEnabled;
        CoachCheck.IsChecked = _settings.CoachEnabled;
        CoachOverlayCheck.IsChecked = _settings.CoachOverlay;
        EchoCheck.IsChecked = _settings.EchoCancellation;
        IsolateCheck.IsChecked = _settings.VoiceIsolation;
        VirtualHint.Text = "The MeetWise Microphone does not exist yet. Press \"Create MeetWise Microphone\": Windows asks for permission once, and may need a restart to show the new device.";

        var outs = new AudioDeviceService().GetRenderDevices().ToList();
        RenderCombo.ItemsSource = outs;
        RenderCombo.SelectedIndex = outs.Count > 0 ? 0 : -1;

        _enhance.StatusChanged += _ => Dispatcher.BeginInvoke(RefreshMicCard);
        _enhance.Downgraded += mode => Dispatcher.BeginInvoke(() => StatusText.Text = $"Your PC could not keep up, so noise cancellation switched to {mode} mode.");
        _micTimer.Tick += (_, _) => RefreshMicCard();
        _micTimer.Start();
        RefreshMicCard();

        if (_settings.AlwaysOnMicrophone && VirtualMic.IsInstalled()) _ = StartMicrophoneAsync();
    }

    private void RefreshMicCard()
    {
        var mic = VirtualMic.FindMic();
        bool installed = mic is not null && VirtualMic.FindFeed() is not null;
        var defaults = DefaultMicrophone.Current();
        bool isDefault = installed && defaults.Communications == mic!.Id;

        VirtualHint.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        CreateMicButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        DefaultMicButton.IsEnabled = installed && !isDefault;
        RestoreMicButton.IsEnabled = isDefault && _settings.PreviousDefaultCommunications is not null;
        AlwaysOnCheck.IsEnabled = installed;
        EnhanceButton.Content = _enhance.IsRunning ? "Stop cleanup" : "Start cleanup";

        string text;
        if (!installed) text = "MeetWise Microphone: not installed.";
        else if (!_enhance.IsRunning) text = "MeetWise Microphone: installed, but not running. Turn on \"Always on\" so apps receive your cleaned voice.";
        else text = $"MeetWise Microphone: ON ({_enhance.Status}). Apps that use it hear your cleaned voice{(isDefault ? " (it is your Windows default microphone)" : "")}. {_enhance.Stats}";
        MicStatusText.Text = text;
        TrayTipChanged?.Invoke(installed && _enhance.IsRunning ? "MeetWise: microphone clean-up is on" : "MeetWise");
    }

    private async Task StartMicrophoneAsync()
    {
        try
        {
            CaptureMicSettings();
            _store.Save(_settings);
            var progress = new Progress<double>(p => StatusText.Text = $"Downloading the noise-cancellation model… {p:P0}");
            await _enhance.StartAsync(_settings, renderDeviceId: null, progress);
            StatusText.Text = "MeetWise Microphone is on.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Starting the microphone failed", ex);
            StatusText.Text = "Could not start the MeetWise Microphone: " + ex.Message;
        }
        RefreshMicCard();
    }

    private async Task RestartMicrophoneIfRunningAsync()
    {
        if (!_enhance.IsRunning) return;
        _enhance.Stop();
        await StartMicrophoneAsync();
    }

    private async void CreateMic_Click(object sender, RoutedEventArgs e)
    {
        var ok = MessageBox.Show(
            "MeetWise will download the free VB-CABLE virtual audio driver by VB-Audio (about 1 MB, from vb-audio.com) and install it. " +
            "Windows will ask for administrator permission once, and it may need a restart before the new device appears.\n\n" +
            VirtualCablePack.Notice + "\n\nContinue?", "Create MeetWise Microphone", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

        CreateMicButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Downloading and installing the virtual microphone…";
            var result = await VirtualMicInstaller.InstallAsync(_http, new Progress<double>(p => StatusText.Text = $"Downloading VB-CABLE… {p:P0}"));
            StatusText.Text = result.Message;
            RefreshMicCard();
            if (result.Outcome is InstallOutcome.Installed or InstallOutcome.AlreadyInstalled)
            {
                var next = MessageBox.Show(result.Message + "\n\nTurn it on now and make it your Windows default microphone, so every app uses it?",
                    "MeetWise Microphone", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (next == MessageBoxResult.Yes) await MakeDefaultAsync();
            }
            else MessageBox.Show(result.Message, "MeetWise Microphone");
        }
        catch (Exception ex)
        {
            AppLog.Error("Virtual microphone setup failed", ex);
            StatusText.Text = "Setup failed: " + ex.Message;
        }
        finally { CreateMicButton.IsEnabled = true; }
    }

    private async void DefaultMic_Click(object sender, RoutedEventArgs e) => await MakeDefaultAsync();

    /// <summary>Starts the route first: making the virtual mic the default while nothing feeds it would leave every app with silence.</summary>
    private async Task MakeDefaultAsync()
    {
        var mic = VirtualMic.FindMic();
        if (mic is null) { StatusText.Text = "The MeetWise Microphone is not installed yet."; return; }

        _settings.AlwaysOnMicrophone = true;
        AlwaysOnCheck.IsChecked = true;
        if (!_enhance.IsRunning) await StartMicrophoneAsync();
        if (!_enhance.IsRunning) { StatusText.Text = "The microphone could not be started, so it was not made the default (apps would hear silence)."; return; }

        var before = DefaultMicrophone.Current();
        if (before.Communications != mic.Id)
        {
            _settings.PreviousDefaultConsole = before.Console;
            _settings.PreviousDefaultMultimedia = before.Multimedia;
            _settings.PreviousDefaultCommunications = before.Communications;
            // remember the real microphone too: the route must keep using it after the default changes
            if (before.Communications is { } real && !VirtualMic.IsVirtualAdapter(VirtualMic.Endpoints(NAudio.CoreAudioApi.DataFlow.Capture).FirstOrDefault(x => x.Id == real)?.Adapter))
                _settings.PhysicalMicId = real;
        }
        _store.Save(_settings);
        StatusText.Text = DefaultMicrophone.Set(mic.Id)
            ? "MeetWise Microphone is now your Windows default microphone. Every app that uses the default hears your cleaned voice."
            : "Windows would not change the default microphone. Choose \"MeetWise Microphone\" in Settings, System, Sound, Input.";
        RefreshMicCard();
    }

    private void RestoreMic_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = new DefaultMicrophone.Snapshot(_settings.PreviousDefaultConsole, _settings.PreviousDefaultMultimedia, _settings.PreviousDefaultCommunications);
        StatusText.Text = DefaultMicrophone.Restore(snapshot) ? "Your previous microphone is the Windows default again." : "Could not restore your previous microphone. Choose it in Settings, System, Sound, Input.";
        RefreshMicCard();
    }

    private async void AlwaysOn_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnMicrophone = AlwaysOnCheck.IsChecked == true;
        _store.Save(_settings);
        if (_settings.AlwaysOnMicrophone) await StartMicrophoneAsync();
        else { _enhance.Stop(); RefreshMicCard(); }
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.StartWithWindows = StartupCheck.IsChecked == true;
            _startup.Set(_settings.StartWithWindows, Environment.ProcessPath!);
            _store.Save(_settings);
        }
        catch (Exception ex) { StatusText.Text = "Could not change the startup setting: " + ex.Message; StartupCheck.IsChecked = _startup.IsEnabled; }
    }

    private async void VoiceProfile_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        var window = new VoiceProfileWindow(_settings, _http) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        if (!window.Changed) return;
        IsolateCheck.IsChecked = _settings.VoiceIsolation;
        _store.Save(_settings);
        await RestartMicrophoneIfRunningAsync(); // apply the new voice right away
    }

    /// <summary>Voice lock needs a trained voice; turning it on without one would silently do nothing, so go straight to training.</summary>
    private async void Isolate_Click(object sender, RoutedEventArgs e)
    {
        if (IsolateCheck.IsChecked == true && !VoiceProfile.IsTrained())
        {
            IsolateCheck.IsChecked = false;
            MessageBox.Show("Voice lock needs to learn your voice first. It takes about a minute: you read six short sentences.", "MeetWise");
            VoiceProfile_Click(sender, e);
            return;
        }
        SaveSettings();
        await RestartMicrophoneIfRunningAsync();
    }

    private void Link_Navigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    /// <summary>The real microphone's id is remembered so the route never picks the virtual one.</summary>
    private void CaptureMicSettings()
    {
        if (MicCombo.SelectedItem is AudioDeviceInfo m && m.Id != AudioDeviceService.DefaultDeviceId) _settings.PhysicalMicId = m.Id;
        else if (VirtualMic.WindowsDefaultCaptureId() is { } d && VirtualMic.FindMic()?.Id != d) _settings.PhysicalMicId = d;
        _settings.CoachEnabled = CoachCheck.IsChecked == true;
        _settings.CoachOverlay = CoachOverlayCheck.IsChecked == true;
        _settings.AlwaysOnMicrophone = AlwaysOnCheck.IsChecked == true;
    }

    /// <summary>Closing while MeetWise Microphone is the Windows default would leave every app with silence: offer to switch back.</summary>
    public bool ConfirmClose()
    {
        var mic = VirtualMic.FindMic();
        if (mic is null || DefaultMicrophone.Current().Communications != mic.Id) return true;
        var answer = MessageBox.Show(
            "MeetWise Microphone is your Windows default microphone. If MeetWise closes, apps will hear silence.\n\nSwitch back to your previous microphone now?",
            "MeetWise", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes)
            DefaultMicrophone.Restore(new DefaultMicrophone.Snapshot(_settings.PreviousDefaultConsole, _settings.PreviousDefaultMultimedia, _settings.PreviousDefaultCommunications));
        return true;
    }

    // ---- English coach ----

    private async Task StartCoachAsync()
    {
        if (!_settings.CoachEnabled || _settings.SpokenLanguage is not ("en" or "auto")) return;
        try
        {
            var manager = new ModelManager(_http);
            await manager.EnsureAsync(ModelCatalog.PunctuationEnglish, new Progress<double>(p => StatusText.Text = $"Downloading the coach's punctuation model… {p:P0}"));
            var punct = new EnglishPunctuator(manager.PathFor(ModelCatalog.PunctuationEnglish));
            StatusText.Text = "Preparing the AI model for the English coach…";
            var llm = await GetLlmAsync();
            if (!_ctx.Session.IsRecording) { punct.Dispose(); return; } // the session ended while the model was loading

            _punct = punct;
            _coachLog.Clear();
            var session = new CoachSession(new SpeechCoach(llm), punct.Restore);
            session.Feedback += fb => Dispatcher.BeginInvoke(() => OnCoachFeedback(fb));
            _coach = session;
            if (_settings.CoachOverlay)
            {
                _overlay ??= new CoachOverlayWindow();
                _overlay.Reset();
                _overlay.Show();
            }
            StatusText.Text = "English coach is on: corrections appear privately on your screen.";
        }
        catch (Exception ex)
        {
            AppLog.Error("English coach could not start", ex);
            StatusText.Text = "The English coach could not start: " + ex.Message;
        }
    }

    private void OnCoachFeedback(CoachFeedback fb)
    {
        _coachLog.Add(fb);
        _overlay?.Add(fb);
        if (_ctx.Session.ItemId != 0)
            _db.AddCoaching(_ctx.Session.ItemId, fb.StartSec, fb.Original, fb.Corrected, fb.Ok, JsonSerializer.Serialize(fb.Issues), fb.WordsPerMinute);
    }

    private string? StopCoach()
    {
        var session = _coach;
        _coach = null;
        session?.Dispose();
        _punct?.Dispose();
        _punct = null;
        _overlay?.Hide();
        if (_coachLog.Count == 0) return null;
        var report = CoachReportBuilder.Build(_coachLog);
        var top = report.Recurring.Count > 0 ? $" Most repeated: {report.Recurring[0].Pattern} ({report.Recurring[0].Count}x)." : "";
        var pace = report.AverageWpm is { } w ? $" Average pace {w:0} words/min." : "";
        return $"English coach: {report.Sentences} sentence(s) reviewed, {report.WithIssues} with tips.{top}{pace}";
    }
}
