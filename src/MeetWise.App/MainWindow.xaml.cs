using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MeetWise.Audio;
using MeetWise.Core.Logging;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;
using MeetWise.Speech;
using NAudio.CoreAudioApi;

namespace MeetWise.App;

/// <summary>
/// The only window: one switch for the clean microphone, the effects, level meters and the two tests that matter (hear yourself exactly as
/// Zoom/Teams hear you; check that the virtual cable carries audio). All audio runs in the <see cref="AudioHost"/> process; this window only
/// edits settings and asks the host to apply them, so closing it never stops the microphone.
/// </summary>
public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly StartupRegistration _startup = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly DispatcherTimer _applyTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private AppSettings _settings;
    private bool _loading = true;
    private MicStatus? _status;
    private bool _hostUp;
    private float[]? _raw, _clean;
    private int _rawRate, _cleanRate;
    private bool _meterBusy, _statusBusy;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _store.Load();
        Loc.Instance.Language = _settings.UiLanguage;

        LanguageBox.ItemsSource = new[] { new { Text = "English", Value = "en" }, new { Text = "Português (BR)", Value = "pt" } };
        LanguageBox.SelectedValue = _settings.UiLanguage == "pt" ? "pt" : "en";
        FillNoiseModes();
        LoadMics();
        EngineBroadcast.IsChecked = _settings.Cleanup == CleanupEngine.NvidiaBroadcast;
        EngineMeetWise.IsChecked = !EngineBroadcast.IsChecked;
        ApplyEngineUi();
        NoiseCheck.IsChecked = _settings.NoiseCancellation != NoiseMode.Off;
        NoiseModeBox.SelectedValue = _settings.NoiseCancellation == NoiseMode.Off ? NoiseMode.Max : _settings.NoiseCancellation;
        StrengthSlider.Value = _settings.NoiseStrength;
        StrengthValue.Text = $"{_settings.NoiseStrength}%";
        EchoCheck.IsChecked = _settings.EchoCancellation;
        VoicesCheck.IsChecked = _settings.VoiceIsolation && VoiceProfile.IsTrained();
        GainCheck.IsChecked = _settings.AutoGain;
        StartupCheck.IsChecked = _startup.IsEnabled;
        AccentCheck.IsEnabled = AccentPack.IsInstalled;
        AccentCheck.IsChecked = _settings.AccentAssist && AccentPack.IsInstalled;
        AccentSlider.Value = _settings.AccentStrength;
        AccentValue.Text = $"{_settings.AccentStrength}%";
        AccentHint.Text = AccentPack.IsInstalled ? Loc.T("accent.hint.ready") : Loc.T("accent.hint.missing", AccentPack.Missing() ?? "");
        UpdateVoicesHint();
        _loading = false;

        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _meterTimer.Tick += async (_, _) => await RefreshMeterAsync();
        _applyTimer.Tick += async (_, _) => { _applyTimer.Stop(); await ApplyAsync(); };
        Loaded += async (_, _) =>
        {
            ShowState();
            _hostUp = await HostLink.EnsureHostAsync();
            if (!_hostUp) AppLog.Warn("The audio host did not start.");
            await RefreshStatusAsync();
            _statusTimer.Start();
            _meterTimer.Start();
        };
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
    }

    // ---------- settings ----------

    private void FillNoiseModes()
    {
        var sel = NoiseModeBox.SelectedValue;
        NoiseModeBox.ItemsSource = new[]
        {
            new { Text = Loc.T("noise.max"), Value = NoiseMode.Max },
            new { Text = Loc.T("noise.balanced"), Value = NoiseMode.Balanced },
            new { Text = Loc.T("noise.light"), Value = NoiseMode.Light }
        };
        NoiseModeBox.SelectedValue = sel ?? NoiseMode.Max;
    }

    private void LoadMics()
    {
        var mics = new AudioDeviceService().GetMicrophones().ToList();
        MicCombo.ItemsSource = mics;
        var wanted = _settings.MicrophoneId;
        MicCombo.SelectedItem = mics.FirstOrDefault(m => m.Id == wanted) ?? mics.FirstOrDefault();
    }

    private void ReadControls()
    {
        _settings.Cleanup = EngineBroadcast.IsChecked == true ? CleanupEngine.NvidiaBroadcast : CleanupEngine.MeetWise;
        _settings.NoiseCancellation = NoiseCheck.IsChecked == true ? (NoiseMode)(NoiseModeBox.SelectedValue ?? NoiseMode.Max) : NoiseMode.Off;
        _settings.NoiseStrength = (int)StrengthSlider.Value;
        _settings.EchoCancellation = EchoCheck.IsChecked == true;
        _settings.VoiceIsolation = VoicesCheck.IsChecked == true;
        _settings.AutoGain = GainCheck.IsChecked == true;
        _settings.AccentAssist = AccentCheck.IsChecked == true;
        _settings.AccentStrength = (int)AccentSlider.Value;
        if (MicCombo.SelectedItem is AudioDeviceInfo m)
        {
            _settings.MicrophoneId = m.Id;
            if (m.Id != AudioDeviceService.DefaultDeviceId) _settings.PhysicalMicId = m.Id;
        }
    }

    /// <summary>Saves the settings and tells the host to apply them (it restarts the route with the new chain).</summary>
    private async Task ApplyAsync()
    {
        ReadControls();
        _store.Save(_settings);
        if (await HostLink.SendAsync("reload") is null)
        {
            _hostUp = await HostLink.EnsureHostAsync();
            if (_hostUp) await HostLink.SendAsync("reload");
        }
        await RefreshStatusAsync();
    }

    private void ScheduleApply()
    {
        if (_loading) return;
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e) => ScheduleApply();

    private void Strength_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StrengthValue is not null) StrengthValue.Text = $"{(int)e.NewValue}%";
        ScheduleApply();
    }

    private void Mic_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => ScheduleApply();

    private void Language_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedValue is not string lang) return;
        Loc.Instance.Language = lang;
        _settings.UiLanguage = lang;
        if (_loading) return;
        _store.Save(_settings);
        _loading = true;
        FillNoiseModes();
        _loading = false;
        UpdateVoicesHint();
        ShowState();
        _ = HostLink.SendAsync("reload"); // the tray menu follows the language
    }

    private async void Power_Click(object sender, RoutedEventArgs e)
    {
        // "on" is remembered: the host keeps the microphone on (and starts it with Windows) until it is switched off here or in the tray
        _settings.AlwaysOnMicrophone = _status?.Running != true;
        PowerButton.IsEnabled = false;
        await ApplyAsync();
        PowerButton.IsEnabled = true;
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.StartWithWindows = StartupCheck.IsChecked == true;
            _startup.Set(_settings.StartWithWindows, Environment.ProcessPath!);
            _store.Save(_settings);
        }
        catch (Exception ex) { StartupCheck.IsChecked = _startup.IsEnabled; MessageBox.Show(ex.Message, "MeetWise"); }
    }

    // ---------- background voices ----------

    private void Engine_Click(object sender, RoutedEventArgs e)
    {
        ApplyEngineUi();
        ScheduleApply();
    }

    /// <summary>With NVIDIA Broadcast cleaning the microphone, MeetWise's own noise and background-voice removal are switched off here and the
    /// microphone is Broadcast's. Speaker-echo cancellation and automatic volume stay available (Broadcast does not cancel speaker echo).</summary>
    private void ApplyEngineUi()
    {
        var broadcastMic = VirtualMic.FindBroadcastMic();
        EngineBroadcast.IsEnabled = broadcastMic is not null || EngineBroadcast.IsChecked == true;
        EngineBroadcast.ToolTip = broadcastMic is null ? Loc.T("fx.engine.nobroadcast") : null;
        bool broadcast = EngineBroadcast.IsChecked == true;
        foreach (var c in new UIElement[] { NoiseCheck, NoiseModeBox, StrengthSlider, VoicesCheck, TrainButton, MicCombo }) c.IsEnabled = !broadcast;
        EngineHint.Text = !broadcast ? ""
            : broadcastMic is null ? Loc.T("fx.engine.missing")
            : Loc.T(VirtualMic.BroadcastAppRunning() ? "fx.engine.broadcast" : "fx.engine.notrunning");
    }

    private void UpdateVoicesHint()
        => VoicesHint.Text = VoiceProfile.IsTrained() ? Loc.T("fx.voices.hint") : Loc.T("fx.voices.untrained");

    private void Voices_Click(object sender, RoutedEventArgs e)
    {
        if (VoicesCheck.IsChecked == true && !VoiceProfile.IsTrained())
        {
            VoicesCheck.IsChecked = false;
            Train_Click(sender, e);
            return;
        }
        ScheduleApply();
    }

    private void Train_Click(object sender, RoutedEventArgs e)
    {
        ReadControls();
        var window = new VoiceProfileWindow(_settings, _http) { Owner = this };
        window.ShowDialog();
        UpdateVoicesHint();
        if (!window.Changed) return;
        VoicesCheck.IsChecked = _settings.VoiceIsolation && VoiceProfile.IsTrained();
        ScheduleApply();
    }

    private void Accent_Click(object sender, RoutedEventArgs e) => ScheduleApply();

    private void AccentStrength_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AccentValue is not null) AccentValue.Text = $"{(int)e.NewValue}%";
        ScheduleApply();
    }

    // ---------- virtual cable ----------

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var ok = MessageBox.Show(Loc.T("mic.install.confirm", VirtualCablePack.Notice), Loc.T("mic.install"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        InstallButton.IsEnabled = false;
        try
        {
            TestText.Text = Loc.T("mic.installing");
            var result = await VirtualMicInstaller.InstallAsync(_http, new Progress<double>(p => TestText.Text = Loc.T("mic.busy.download", p.ToString("P0"))));
            TestText.Text = result.Message;
            if (result.Outcome is InstallOutcome.Installed or InstallOutcome.AlreadyInstalled)
            {
                _settings.AlwaysOnMicrophone = true;
                await ApplyAsync();
            }
        }
        catch (Exception ex) { AppLog.Error("Install failed", ex); TestText.Text = ex.Message; }
        finally { InstallButton.IsEnabled = true; }
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        RepairButton.IsEnabled = false;
        try
        {
            // a cable that was only disabled in Windows needs no administrator rights: just switch it back on
            if (await Task.Run(VirtualMic.GetState) == CableState.Disabled)
            {
                if (MessageBox.Show(Loc.T("mic.enable.confirm"), "MeetWise", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
                int n = await Task.Run(VirtualMic.EnableDisabledEndpoints);
                await Task.Delay(800);
                if (await Task.Run(VirtualMic.GetState) == CableState.Ready)
                {
                    TestText.Text = Loc.T("mic.enabled", n);
                    await ApplyAsync();
                    return;
                }
            }
            TestText.Text = Loc.T("mic.repairing");
            var result = await VirtualMicInstaller.RepairAsync(_http);
            TestText.Text = result.Message;
            await ApplyAsync();
        }
        catch (Exception ex) { AppLog.Error("Repair failed", ex); TestText.Text = ex.Message; }
        finally { RepairButton.IsEnabled = true; }
    }

    /// <summary>Turns the microphone on first: a default microphone nobody feeds would give every app silence.</summary>
    private async void Default_Click(object sender, RoutedEventArgs e)
    {
        var mic = await Task.Run(VirtualMic.FindMic);
        if (mic is null) return;
        if (_status?.Running != true)
        {
            _settings.AlwaysOnMicrophone = true;
            await ApplyAsync();
            for (int i = 0; i < 30 && _status?.Running != true; i++) { await Task.Delay(500); await RefreshStatusAsync(); }
            if (_status?.Running != true) { TestText.Text = Loc.T("mic.default.notrunning"); return; }
        }
        var before = DefaultMicrophone.Current();
        if (before.Communications != mic.Id)
        {
            _settings.PreviousDefaultConsole = before.Console;
            _settings.PreviousDefaultMultimedia = before.Multimedia;
            _settings.PreviousDefaultCommunications = before.Communications;
            if (before.Communications is { } real && !VirtualMic.IsVirtualAdapter(VirtualMic.Endpoints(DataFlow.Capture).FirstOrDefault(x => x.Id == real)?.Adapter))
                _settings.PhysicalMicId = real;
            _store.Save(_settings);
        }
        TestText.Text = DefaultMicrophone.Set(mic.Id) ? Loc.T("mic.default.done") : Loc.T("mic.default.failed");
        await RefreshStatusAsync();
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var snap = new DefaultMicrophone.Snapshot(_settings.PreviousDefaultConsole, _settings.PreviousDefaultMultimedia, _settings.PreviousDefaultCommunications);
        TestText.Text = DefaultMicrophone.Restore(snap) ? Loc.T("mic.restore.done") : Loc.T("mic.restore.failed");
        await RefreshStatusAsync();
    }

    // ---------- status ----------

    private bool _isDefault;

    private async Task RefreshStatusAsync()
    {
        if (_statusBusy) return;
        _statusBusy = true;
        try
        {
            var reply = await HostLink.SendAsync("status", TimeSpan.FromSeconds(2));
            _hostUp = reply is not null;
            _status = reply?.Status;
            _busy = reply?.Busy;
            _hostError = reply?.Error;
            _isDefault = await Task.Run(() => VirtualMic.FindMic() is { } m && DefaultMicrophone.Current().Communications == m.Id);
            if (_status is null) _cable = await Task.Run(VirtualMic.GetState);
            ShowState();
        }
        finally { _statusBusy = false; }
    }

    private string? _busy, _hostError;
    private CableState _cable = CableState.Ready;

    private void ShowState()
    {
        var cable = _status?.Cable ?? _cable;
        bool running = _status?.Running == true && _status.State == "running";
        bool waiting = _status?.Running == true && !running;
        string title, detail;
        Brush dot;
        if (!_hostUp && !_loading && IsLoaded) { title = Loc.T("state.nohost"); detail = Loc.T("state.nohost.detail"); dot = Brushes.Gray; }
        else if (_busy is not null) { title = _busy; detail = ""; dot = Brushes.Goldenrod; }
        else if (cable == CableState.Missing) { title = Loc.T("state.notinstalled"); detail = Loc.T("state.notinstalled.detail"); dot = Brushes.Gray; }
        else if (VirtualMic.IsBroken(cable)) { title = Loc.T(cable == CableState.Disabled ? "state.disabled" : "state.broken"); detail = Loc.T("state.broken.detail"); dot = Brushes.IndianRed; }
        else if (running) { title = Loc.T("state.on"); detail = Loc.T("state.on.detail", _status!.PhysicalMic ?? "?", _status.Applied); dot = Brushes.LimeGreen; }
        else if (waiting) { title = Loc.T("state.waiting"); detail = _status!.State; dot = Brushes.Goldenrod; }
        else { title = Loc.T("state.off"); detail = Loc.T("state.off.detail"); dot = Brushes.Gray; }
        if (_isDefault && running) detail += Environment.NewLine + Loc.T("state.isdefault");
        else if (_isDefault && !running) detail += Environment.NewLine + Loc.T("state.defaultsilent");

        StateTitle.Text = title;
        StateDetail.Text = detail;
        StateDot.Fill = dot;
        PowerButton.Content = Loc.T(_status?.Running == true ? "mic.turnoff" : "mic.turnon");
        PowerButton.IsEnabled = cable is not CableState.Missing;
        InstallButton.Visibility = cable == CableState.Missing ? Visibility.Visible : Visibility.Collapsed;
        RepairButton.Visibility = VirtualMic.IsBroken(cable) ? Visibility.Visible : Visibility.Collapsed;
        DefaultButton.IsEnabled = cable == CableState.Ready && !_isDefault;
        RestoreButton.IsEnabled = _isDefault && _settings.PreviousDefaultCommunications is not null;
        ZoomTestButton.IsEnabled = running;
        CheckButton.IsEnabled = running;

        var problems = new List<string>();
        if (_hostError is not null) problems.Add(_hostError);
        if (_status?.LastProblem is { } lp && lp != _hostError) problems.Add(lp);
        if (_status?.Fixes is { Count: > 0 } fx) problems.Add(Loc.T("state.fixed", string.Join("; ", fx)));
        AccentStatus.Text = _status?.Accent is { } acc ? Loc.T("accent.status", acc) : "";
        ProblemText.Text = string.Join(Environment.NewLine, problems);
        ProblemText.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshMeterAsync()
    {
        if (_meterBusy || !_hostUp || WindowState == WindowState.Minimized) return;
        _meterBusy = true;
        try
        {
            var m = (await HostLink.SendAsync("meter", TimeSpan.FromMilliseconds(500)))?.Meter;
            InMeter.Value = Meter(m?.InputPeak ?? 0, InMeter.Value);
            OutMeter.Value = Meter(m?.OutputPeak ?? 0, OutMeter.Value);
            StatsText.Text = m is null ? "" : Loc.T("meter.stats", m.LatencyMs.ToString("0"), m.Load.ToString("P0"), m.GainDb.ToString("+0;-0;0"));
        }
        finally { _meterBusy = false; }
    }

    /// <summary>Peak to a -60..0 dB bar with a falling needle (fast up, slow down).</summary>
    private static double Meter(float peak, double previous)
    {
        double v = Math.Clamp((AudioMetrics.Db(peak) + 60) / 60, 0, 1);
        return v >= previous ? v : Math.Max(v, previous - 0.04);
    }

    // ---------- tests ----------

    /// <summary>
    /// Records 6 seconds from the real microphone and, at the same time, from the MeetWise Microphone (what Zoom/Teams record). Playing the
    /// second one is exactly what the other people in the call hear.
    /// </summary>
    private async void ZoomTest_Click(object sender, RoutedEventArgs e)
    {
        ZoomTestButton.IsEnabled = PlayRawButton.IsEnabled = PlayCleanButton.IsEnabled = false;
        TestProgress.Visibility = Visibility.Visible;
        var progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        var start = DateTime.UtcNow;
        progressTimer.Tick += (_, _) => TestProgress.Value = Math.Min(1, (DateTime.UtcNow - start).TotalSeconds / 6);
        progressTimer.Start();
        try
        {
            TestText.Text = Loc.T("test.speak");
            ClipRecorder? raw = null;
            try { raw = new ClipRecorder(_settings.MicrophoneId, _settings.PhysicalMicId); raw.Start(); }
            catch (Exception ex) { AppLog.Warn("Raw recording unavailable: " + ex.Message); raw = null; }
            var (clean, rate) = await MicService.RecordVirtualMicAsync(TimeSpan.FromSeconds(6));
            _raw = raw?.Stop();
            _rawRate = 16000;
            raw?.Dispose();
            _clean = clean;
            _cleanRate = rate;
            double peak = AudioMetrics.PeakDb(clean);
            if (peak < -90) TestText.Text = Loc.T("test.silent");
            else
            {
                var (noise, speech) = AudioMetrics.Profile(clean, rate);
                TestText.Text = Loc.T("test.done", speech.ToString("0"), noise.ToString("0"));
                if (_raw is { Length: > 0 })
                {
                    var (rn, rs) = AudioMetrics.Profile(_raw, _rawRate);
                    TestText.Text += Environment.NewLine + Loc.T("test.compare", (rn - noise).ToString("0"));
                }
            }
            PlayCleanButton.IsEnabled = true;
            PlayRawButton.IsEnabled = _raw is { Length: > 0 };
        }
        catch (Exception ex) { AppLog.Error("Zoom test failed", ex); TestText.Text = ex.Message; }
        finally
        {
            progressTimer.Stop();
            TestProgress.Visibility = Visibility.Collapsed;
            ZoomTestButton.IsEnabled = _status?.Running == true;
        }
    }

    private void PlayRaw_Click(object sender, RoutedEventArgs e) { if (_raw is not null) _ = PlayAsync(_raw, _rawRate); }
    private void PlayClean_Click(object sender, RoutedEventArgs e) { if (_clean is not null) _ = PlayAsync(_clean, _cleanRate); }

    private static async Task PlayAsync(float[] samples, int rate)
    {
        try
        {
            await Task.Run(async () =>
            {
                using var en = new MMDeviceEnumerator();
                using var device = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                using var player = new PcmPlayer(device, rate);
                player.Enqueue(samples);
                await Task.Delay(TimeSpan.FromSeconds(samples.Length / (double)rate + 0.4));
            });
        }
        catch (Exception ex) { AppLog.Warn("Playback failed: " + ex.Message); }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        TestText.Text = Loc.T("test.checking");
        var reply = await HostLink.SendAsync("check", TimeSpan.FromSeconds(10));
        TestText.Text = reply?.Check is { } c ? (c.Passed ? "✔ " : "✖ ") + c.Message : Loc.T("state.nohost");
        CheckButton.IsEnabled = _status?.Running == true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _statusTimer.Stop();
        _meterTimer.Stop();
        if (_applyTimer.IsEnabled) { _applyTimer.Stop(); ReadControls(); _store.Save(_settings); _ = HostLink.SendAsync("reload"); }
        base.OnClosing(e);
    }
}
