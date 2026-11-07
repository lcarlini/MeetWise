using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using MeetWise.Audio;
using MeetWise.Core;
using MeetWise.Core.Hardware;
using MeetWise.Core.Logging;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;
using MeetWise.Intelligence;
using MeetWise.Storage;

namespace MeetWise.App;

/// <summary>
/// The Studio shell: a navigation rail and five pages (Record, Subtitles, AI Assistant, Library, Meetings), the tray icon, call detection and
/// calendar reminders, and the global Ctrl+Shift+R shortcut. All the real work lives in the pages and the engines.
/// </summary>
public partial class MainWindow : Window
{
    private sealed record Choice(string Value, string Text) { public override string ToString() => Text; }

    private readonly StudioContext _ctx;
    private readonly RecordPage _record;
    private readonly SubtitlesPage _subtitles;
    private readonly AssistantPage _assistant;
    private readonly LibraryPage _library;
    private readonly MeetingsPage _meetings;
    private readonly Dictionary<string, UserControl> _pages = new();
    private readonly TaskbarIcon _tray;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly CalendarService _calendar;
    private readonly MeetingDetector _detector = new();
    private ReminderScheduler _reminders;
    private Func<Task>? _balloonAction;
    private GlobalHotKey? _hotKey;
    private bool _loadingLanguage;

    public MainWindow()
    {
        InitializeComponent();

        var store = new SettingsStore();
        var settings = store.Load();
        Loc.Instance.Language = settings.UiLanguage;
        var hardware = HardwareDetector.Detect(settings.TierOverride);
        var http = new HttpClient();
        var db = new MeetingDatabase(AppPaths.Database);
        var secrets = new SecretStore();
        _ctx = new StudioContext
        {
            Store = store, Settings = settings, Hardware = hardware, Db = db, Http = http, Secrets = secrets,
            Llm = new LlmFactory(http, secrets),
            Session = new RecordingSession(db, http, hardware),
            Enhance = new EnhancementController(http),
            Imports = ImportService.Create(db, http, hardware, () => settings.Glossary)
        };
        _ctx.Glossary = new Glossary(settings.Glossary);
        _ctx.Session.TextFilter = t => _ctx.Glossary.Apply(t);
        _ctx.Navigate = ShowPage;
        _ctx.Toast += ShowToast;

        var gpu = hardware.Gpus.Count > 0 ? $"{hardware.Gpus[0].Name} ({hardware.Gpus[0].VramMb / 1024.0:0.#} GB)" : "no dedicated GPU";
        HardwareText.Text = $"{gpu}\n{hardware.CpuThreads} threads · tier {hardware.Tier}";

        _record = new RecordPage(_ctx);
        _subtitles = new SubtitlesPage(_ctx);
        _assistant = new AssistantPage(_ctx);
        _library = new LibraryPage(_ctx);
        _meetings = new MeetingsPage(_ctx);
        foreach (var (name, page) in new (string, UserControl)[] { ("record", _record), ("subtitles", _subtitles), ("assistant", _assistant), ("library", _library), ("meetings", _meetings) })
        {
            _pages[name] = page;
            page.Visibility = Visibility.Collapsed;
            Pages.Children.Add(page);
        }
        ShowPage("record");

        FillLanguages();
        _calendar = new CalendarService(http);
        _reminders = new ReminderScheduler(TimeSpan.FromMinutes(settings.ReminderMinutes));
        _tray = new TaskbarIcon { Icon = AppIcon(), ToolTipText = "MeetWise" };
        _tray.TrayMouseDoubleClick += (_, _) => RestoreFromTray();
        _tray.TrayBalloonTipClicked += async (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            RestoreFromTray();
            if (action is not null) await action();
        };
        _meetings.TrayTipChanged += t => _tray.ToolTipText = t;
        _tick.Tick += async (_, _) => await TickAsync();
        _tick.Start();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBorder.Visibility = Visibility.Collapsed; };
        _ = RecoverUnfinishedVideosAsync();

        SourceInitialized += (_, _) => OnHandleReady();
    }

    /// <summary>The MeetWise icon for the tray (embedded as a WPF resource); the generic icon if it cannot be read.</summary>
    private static System.Drawing.Icon AppIcon()
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/meetwise.ico"))!.Stream;
            return new System.Drawing.Icon(stream);
        }
        catch (Exception) { return System.Drawing.SystemIcons.Application; }
    }

    private void OnHandleReady()
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        // like RecBox, the app's own window stays out of its recordings (switch off in Settings to record MeetWise itself)
        if (_ctx.Settings.HideWindowFromCapture) CaptureGuards.ExcludeFromCapture(source.Handle);
        _hotKey = new GlobalHotKey(source, GlobalHotKey.Control | GlobalHotKey.Shift, 0x52 /* R */, () => Dispatcher.BeginInvoke(async () => await _record.ToggleAsync()));
        if (!_hotKey.IsRegistered) ShowToast("Ctrl+Shift+R is used by another program; use the Record button.");
    }

    // ---- navigation ----

    private void Nav_Click(object sender, RoutedEventArgs e) => ShowPage((string)((RadioButton)sender).CommandParameter);

    private void ShowPage(string name)
    {
        foreach (var (key, page) in _pages) page.Visibility = key == name ? Visibility.Visible : Visibility.Collapsed;
        var nav = name switch { "record" => NavRecord, "subtitles" => NavSubtitles, "assistant" => NavAssistant, "library" => NavLibrary, _ => NavMeetings };
        nav.IsChecked = true;
    }

    private void FillLanguages()
    {
        _loadingLanguage = true;
        LanguageBox.ItemsSource = new[] { new Choice("en", "English"), new Choice("pt", "Português"), new Choice("es", "Español") };
        LanguageBox.SelectedValue = Loc.Instance.Language;
        _loadingLanguage = false;
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLanguage || LanguageBox.SelectedValue is not string code) return;
        Loc.Instance.Language = code;
        _ctx.Settings.UiLanguage = code;
        _ctx.Save();
    }

    private void ShowToast(string text)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ToastText.Text = text;
            ToastBorder.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        });
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        NavSettings.IsChecked = false;
        _meetings.SaveSettings();
        var dlg = new SettingsWindow(_ctx.Settings, _ctx.Secrets) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _ctx.Save();
            _ctx.Glossary = new Glossary(_ctx.Settings.Glossary);
            _reminders = new ReminderScheduler(TimeSpan.FromMinutes(_ctx.Settings.ReminderMinutes));
            ShowToast("Settings saved.");
        }
    }

    // ---- tray, call detection, calendar ----

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _ctx.Settings.MinimizeToTray) Hide();
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
            var s = _ctx.Settings;
            if (s.AutoDetectMeetings && !_ctx.Session.IsRecording && _detector.Poll() is { } call)
                Notify("MeetWise", $"{call.Process} is using your microphone. Click to start recording.", async () =>
                {
                    ShowPage("record");
                    if (!_ctx.Session.IsRecording) await _record.ToggleAsync();
                });

            if (!string.IsNullOrWhiteSpace(s.CalendarUrl))
            {
                var upcoming = await _calendar.UpcomingAsync(s.CalendarUrl, DateTime.UtcNow, TimeSpan.FromHours(12));
                foreach (var r in _reminders.Due(upcoming, DateTime.UtcNow))
                {
                    var evt = r.Event;
                    var mins = Math.Max(0, (int)Math.Round(r.Remaining.TotalMinutes));
                    Notify($"Starting in {mins} min", $"{evt.Title}. Click for your brief.", async () =>
                    {
                        ShowPage("meetings");
                        await _meetings.ShowBriefAsync(evt);
                    });
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("Background check failed: " + ex.Message); }
    }

    /// <summary>After a crash or power loss the MKV capture is still on disk: turn any orphaned one into an MP4 the next time the app starts.</summary>
    private async Task RecoverUnfinishedVideosAsync()
    {
        try
        {
            var ffmpeg = FFmpegPack.Find();
            if (ffmpeg is null) return;
            foreach (var mp4 in await VideoRecorder.RecoverOrphansAsync(ffmpeg, _ctx.Settings.OutputFolder))
                AppLog.Info("Recovered unfinished video: " + mp4);
        }
        catch (Exception ex) { AppLog.Warn("Video recovery skipped: " + ex.Message); }
    }

    // ---- closing ----

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_ctx.Session.IsRecording)
        {
            var r = MessageBox.Show("A recording is in progress. Stop, save it and exit?", "MeetWise", MessageBoxButton.YesNo);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
            // run on a pool thread: the session's awaits would otherwise try to resume on this (blocked) UI thread
            Task.Run(() => _ctx.Session.StopAsync()).Wait(TimeSpan.FromMinutes(3));
        }
        if (!_meetings.ConfirmClose()) { e.Cancel = true; return; }

        _hotKey?.Dispose();
        _tick.Stop();
        _meetings.SaveSettings();
        _meetings.Shutdown();
        _library.Shutdown();
        _tray.Dispose();
        _ctx.Enhance.Dispose();
        _ctx.Imports.Dispose();
        _ctx.Session.Dispose();
        base.OnClosing(e);
    }
}
