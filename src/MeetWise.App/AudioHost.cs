using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using MeetWise.Audio;
using MeetWise.Core.Logging;
using MeetWise.Core.Settings;

namespace MeetWise.App;

/// <summary>
/// The background process that owns the clean microphone. It has no window, only a tray icon, so closing the MeetWise window never silences
/// the MeetWise Microphone in Zoom/Teams. It starts with Windows (when enabled), keeps the microphone on whenever "on" is chosen, and answers
/// the window over <see cref="HostLink"/>. Only "Quit" in the tray stops it.
/// </summary>
public sealed class AudioHost : IDisposable
{
    private readonly Application _app;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly SettingsStore _store = new();
    private readonly MicService _mic;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private TaskbarIcon? _tray;
    private MenuItem? _toggleItem;
    private string? _busy;
    private string? _startError;

    public AudioHost(Application app)
    {
        _app = app;
        _mic = new MicService(_http);
        _mic.Changed += () => _app.Dispatcher.BeginInvoke(UpdateTray);
    }

    public void Run()
    {
        _app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var settings = _store.Load();
        Loc.Instance.Language = settings.UiLanguage;
        CreateTray();
        _ = Task.Run(() => ServeAsync(_cts.Token));
        _ = ApplyAsync();
        // an older MeetWise registered "--minimized" at sign-in: point it at the host instead (same program path)
        try { if (new StartupRegistration().MigrateLegacy()) AppLog.Info("Startup entry now starts the background microphone."); }
        catch (Exception ex) { AppLog.Warn("Could not update the startup entry: " + ex.Message); }
    }

    /// <summary>Re-reads the settings and starts, restarts or stops the microphone to match them.</summary>
    private async Task ApplyAsync()
    {
        await _applyLock.WaitAsync();
        try
        {
            var s = _store.Load();
            Loc.Instance.Language = s.UiLanguage;
            if (!s.AlwaysOnMicrophone) { await Task.Run(_mic.Stop); _startError = null; return; }
            if (VirtualMic.GetState() == CableState.Missing) { await Task.Run(_mic.Stop); _startError = Loc.T("mic.err.notinstalled"); return; }
            _busy = Loc.T("mic.busy.starting");
            var progress = new Progress<double>(p => _busy = Loc.T("mic.busy.download", p.ToString("P0")));
            await _mic.StartAsync(s, progress, _cts.Token);
            _startError = null;
        }
        catch (Exception ex)
        {
            AppLog.Error("Starting the clean microphone failed", ex);
            _startError = ex.Message;
        }
        finally
        {
            _busy = null;
            _applyLock.Release();
            _ = _app.Dispatcher.BeginInvoke(UpdateTray);
        }
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = NamedPipeServerStreamAcl.Create(HostLink.PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, HostLink.CurrentUserOnly());
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { AppLog.Warn("Pipe error: " + ex.Message); await Task.Delay(500, ct).ContinueWith(_ => { }); continue; }
            _ = Task.Run(() => HandleAsync(server, ct));
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(ct);
                var req = line is null ? null : JsonSerializer.Deserialize<HostRequest>(line, HostLink.Json);
                var reply = await ExecuteAsync(req?.Cmd ?? "", ct);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply, HostLink.Json) + "\n");
                await pipe.WriteAsync(bytes, ct);
                await pipe.FlushAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException) { }
        }
    }

    private async Task<HostReply> ExecuteAsync(string cmd, CancellationToken ct)
    {
        switch (cmd)
        {
            case "ping": return new HostReply(true);
            case "status": return new HostReply(true, _startError, _mic.Status(), Busy: _busy);
            case "meter": return new HostReply(true, Meter: _mic.Meter());
            case "reload":
                _ = ApplyAsync();
                return new HostReply(true);
            case "check":
                return new HostReply(true, Check: await _mic.CheckCableAsync(ct));
            case "quit":
                _ = _app.Dispatcher.BeginInvoke(Quit);
                return new HostReply(true);
            default: return new HostReply(false, "unknown command " + cmd);
        }
    }

    private void CreateTray()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = Loc.T("tray.open") };
        open.Click += (_, _) => OpenWindow();
        _toggleItem = new MenuItem { Header = Loc.T("tray.on"), IsCheckable = true };
        _toggleItem.Click += (_, _) => SetOn(_toggleItem.IsChecked);
        var quit = new MenuItem { Header = Loc.T("tray.quit") };
        quit.Click += (_, _) => Quit();
        menu.Items.Add(open);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        _tray = new TaskbarIcon
        {
            IconSource = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/meetwise.ico")),
            ToolTipText = "MeetWise",
            ContextMenu = menu
        };
        _tray.TrayMouseDoubleClick += (_, _) => OpenWindow();
        _tray.TrayLeftMouseUp += (_, _) => OpenWindow();
    }

    private void UpdateTray()
    {
        if (_tray is null || _toggleItem is null) return;
        bool on = _mic.IsRunning;
        _toggleItem.IsChecked = on;
        _tray.ToolTipText = on ? Loc.T("tray.tip.on") : _startError is null ? Loc.T("tray.tip.off") : Loc.T("tray.tip.problem");
    }

    private void SetOn(bool on)
    {
        var s = _store.Load();
        s.AlwaysOnMicrophone = on;
        _store.Save(s);
        _ = ApplyAsync();
    }

    private static void OpenWindow()
    {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false }); }
        catch (Exception ex) { AppLog.Warn("Could not open the window: " + ex.Message); }
    }

    /// <summary>Quitting while the MeetWise Microphone is the Windows default leaves every app with silence: offer to switch back first.</summary>
    private void Quit()
    {
        var s = _store.Load();
        var mic = VirtualMic.FindMic();
        if (mic is not null && DefaultMicrophone.Current().Communications == mic.Id)
        {
            var answer = MessageBox.Show(Loc.T("quit.default"), "MeetWise", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
                DefaultMicrophone.Restore(new DefaultMicrophone.Snapshot(s.PreviousDefaultConsole, s.PreviousDefaultMultimedia, s.PreviousDefaultCommunications));
        }
        // ask an open window to close too: it would only show "not running" afterwards
        try { using var ev = EventWaitHandle.OpenExisting(App.CloseWindowEvent); ev.Set(); } catch (Exception) { }
        Dispose();
        _app.Shutdown();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _tray?.Dispose();
        _tray = null;
        _mic.Dispose();
    }
}
