using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using MeetWise.Audio;
using MeetWise.Core.Logging;

namespace MeetWise.App.Try;

/// <summary>What a quick test produced: a one-line summary, optional original/processed audio to compare by ear, and optional longer text.</summary>
public sealed record QuickResult(string Summary, float[]? Original = null, int OriginalRate = 16000, float[]? Processed = null, int ProcessedRate = 16000, string? Details = null);

/// <summary>What the person gave the test: a recorded clip, an opened file, and/or typed text.</summary>
public sealed record QuickInput(TryClipData? Clip, string? FilePath, string Text, IProgress<string> Status);

/// <summary>How a quick test is started. Labels are Loc keys.</summary>
public sealed class QuickSpec
{
    public required string TitleKey { get; init; }
    public required string HintKey { get; init; }
    /// <summary>Record from a microphone (the id is asked for when recording starts, so it follows the page's current choice).</summary>
    public Func<string>? MicId { get; init; }
    /// <summary>Also record what the PC plays (needed to hear echo cancellation).</summary>
    public Func<bool>? CaptureSystemAudio { get; init; }
    public int Seconds { get; init; } = 10;
    public bool AllowFile { get; init; }
    /// <summary>Shows a text box and a Run button (label key) for tests that work on typed text or need no input at all.</summary>
    public string? RunKey { get; init; }
    public bool ShowText { get; init; }
    public required Func<QuickInput, Task<QuickResult>> Run { get; init; }
}

/// <summary>
/// "Test with my settings": one button next to the settings it tests. It uses whatever is selected on the page (read when the test starts),
/// records or opens the input, and gives ▶ Original / ▶ With my settings plus a plain summary. No choices of its own.
/// </summary>
public partial class QuickTest : UserControl
{
    private QuickSpec? _spec;
    private TryRecording? _rec;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _left;
    private bool _busy;
    private QuickResult? _last;

    public QuickTest()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) =>
        {
            _left--;
            RecordButton.Content = Loc.T("cmp.stop", Math.Max(0, _left));
            if (_left <= 0) await StopAndRunAsync();
        };
        Loc.Instance.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Loc.Language)) ApplyTexts(); };
        Unloaded += (_, _) => TryPlayer.Stop();
    }

    public void Setup(QuickSpec spec)
    {
        _spec = spec;
        RecordButton.Visibility = spec.MicId is null ? Visibility.Collapsed : Visibility.Visible;
        FileButton.Visibility = spec.AllowFile ? Visibility.Visible : Visibility.Collapsed;
        RunButton.Visibility = spec.RunKey is null ? Visibility.Collapsed : Visibility.Visible;
        InputText.Visibility = spec.ShowText ? Visibility.Visible : Visibility.Collapsed;
        bool audio = spec.MicId is not null || spec.AllowFile;
        PlayOriginal.Visibility = PlayResult.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        ApplyTexts();
    }

    private void ApplyTexts()
    {
        if (_spec is null) return;
        TitleText.Text = Loc.T(_spec.TitleKey);
        HintText.Text = Loc.T(_spec.HintKey);
        if (_rec is null) RecordButton.Content = Loc.T("quick.record", _spec.Seconds);
        if (_spec.RunKey is not null) RunButton.Content = Loc.T(_spec.RunKey);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        FileButton.IsEnabled = RunButton.IsEnabled = !busy;
        RecordButton.IsEnabled = !busy || _rec is not null;
    }

    // ---- inputs ----

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_spec?.MicId is null) return;
        if (_rec is not null) { await StopAndRunAsync(); return; }
        if (_busy) return;
        TryPlayer.Stop();
        try
        {
            string mic = _spec.MicId();
            bool loop = _spec.CaptureSystemAudio?.Invoke() == true;
            var rec = await Task.Run(() => new TryRecording(mic, loop)); // opening a device can block: never on the UI thread
            rec.Level += p => Dispatcher.BeginInvoke(() => Level.Value = p);
            _rec = rec;
            _left = _spec.Seconds;
            Level.Visibility = Visibility.Visible;
            RecordButton.Content = Loc.T("cmp.stop", _left);
            PlayOriginal.IsEnabled = PlayResult.IsEnabled = false;
            SummaryText.Text = Loc.T("cmp.speak");
            DetailsText.Visibility = Visibility.Collapsed;
            SetBusy(true);
            _timer.Start();
        }
        catch (Exception ex)
        {
            AppLog.Error("Quick test recording could not start", ex);
            SummaryText.Text = AudioErrors.Describe(ex);
            _rec = null;
        }
    }

    private async Task StopAndRunAsync()
    {
        _timer.Stop();
        var rec = _rec;
        _rec = null;
        if (rec is null) return;
        RecordButton.Content = Loc.T("quick.record", _spec!.Seconds);
        Level.Value = 0;
        Level.Visibility = Visibility.Collapsed;
        try
        {
            var clip = await Task.Run(() => { var c = rec.Stop("recording"); rec.Dispose(); return c; });
            if (clip.Mic.Length < 16000) { SummaryText.Text = Loc.T("try.tooshort"); SetBusy(false); return; }
            TryClip.Set(clip);
            await RunAsync(new QuickInput(clip, null, InputText.Text.Trim(), Progress()));
        }
        catch (Exception ex)
        {
            AppLog.Error("Quick test recording failed", ex);
            SummaryText.Text = AudioErrors.Describe(ex);
            SetBusy(false);
        }
    }

    private async void File_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _spec is null) return;
        var dlg = new OpenFileDialog { Filter = "Audio / video|*.wav;*.mp3;*.m4a;*.flac;*.ogg;*.aac;*.wma;*.mp4;*.mkv;*.mov;*.webm;*.avi|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        SetBusy(true);
        await RunAsync(new QuickInput(null, dlg.FileName, InputText.Text.Trim(), Progress()));
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _spec is null) return;
        SetBusy(true);
        await RunAsync(new QuickInput(null, null, InputText.Text.Trim(), Progress()));
    }

    private IProgress<string> Progress() => new Progress<string>(t => SummaryText.Text = t);

    private async Task RunAsync(QuickInput input)
    {
        TryPlayer.Stop();
        PlayOriginal.IsEnabled = PlayResult.IsEnabled = false;
        DetailsText.Visibility = Visibility.Collapsed;
        try
        {
            var r = await _spec!.Run(input);
            _last = r;
            SummaryText.Text = r.Summary;
            PlayOriginal.IsEnabled = r.Original is not null;
            PlayResult.IsEnabled = r.Processed is not null;
            if (!string.IsNullOrWhiteSpace(r.Details)) { DetailsText.Text = r.Details; DetailsText.Visibility = Visibility.Visible; }
        }
        catch (Exception ex)
        {
            AppLog.Error("Quick test failed: " + _spec!.TitleKey, ex);
            SummaryText.Text = Loc.T("try.failed", ex.Message);
        }
        finally { SetBusy(false); }
    }

    // ---- playback ----

    private void PlayOriginal_Click(object sender, RoutedEventArgs e) => Play(_last?.Original, _last?.OriginalRate ?? 16000);
    private void PlayResult_Click(object sender, RoutedEventArgs e) => Play(_last?.Processed, _last?.ProcessedRate ?? 16000);
    private void Stop_Click(object sender, RoutedEventArgs e) => TryPlayer.Stop();

    private void Play(float[]? samples, int rate)
    {
        if (samples is null) return;
        try { TryPlayer.Play(samples, rate); }
        catch (Exception ex) { SummaryText.Text = Loc.T("try.cantplay", ex.Message); }
    }
}
