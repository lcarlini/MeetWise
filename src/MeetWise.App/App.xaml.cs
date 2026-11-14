using System.Windows;
using MeetWise.Audio;

namespace MeetWise.App;

/// <summary>
/// One executable, three roles:
/// <list type="bullet">
/// <item><c>--audio-host</c>: the background microphone with a tray icon (started with Windows and by the window). It owns the audio.</item>
/// <item>no argument: the window. It only shows and changes settings; closing it never stops the microphone.</item>
/// <item>elevated helpers (install/repair/rename the virtual cable) and <c>--selftest</c>.</item>
/// </list>
/// </summary>
public partial class App : Application
{
    public const string CloseWindowEvent = @"Local\MeetWise.CloseWindow";
    private Mutex? _single;
    private EventWaitHandle? _activate, _close;
    private RegisteredWaitHandle? _activateWait, _closeWait;
    private AudioHost? _host;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var args = e.Args;
        bool Has(string a) => args.Contains(a, StringComparer.OrdinalIgnoreCase);

        // Elevated helper modes: the normal app starts itself with administrator rights for exactly one job and exits.
        if (Has(VirtualMicInstaller.InstallArg)) { Environment.Exit(VirtualMicInstaller.RunInstallHelper()); return; }
        if (Has(VirtualMicInstaller.UninstallArg)) { Environment.Exit(VirtualMicInstaller.RunUninstallHelper()); return; }
        if (Has(VirtualMicInstaller.RepairArg)) { Environment.Exit(VirtualMicInstaller.RunRepairHelper()); return; }
        if (Has(VirtualMicInstaller.RenameArg)) { Environment.Exit(VirtualMicInstaller.RunRenameHelper()); return; }

        if (Has("--selftest"))
        {
            Environment.Exit(await Task.Run(() => SelfTest.RunAsync()));
            return;
        }

        if (Has("--process"))
        {
            // offline: the live chain over a file, for the quality bench
            Environment.Exit(await Task.Run(() => OfflineProcessor.RunAsync(args)));
            return;
        }

        if (Has(HostLink.HostArg) || Has("--minimized"))
        {
            // "--minimized" is what older versions registered to start with Windows: it now means "background microphone only"
            _single = new Mutex(true, @"Local\MeetWise.AudioHost", out bool firstHost);
            if (!firstHost) { Environment.Exit(0); return; }
            _host = new AudioHost(this);
            _host.Run();
            return;
        }

        // One window only: a second launch brings the first one to the front.
        _single = new Mutex(true, @"Local\MeetWise.Window", out bool first);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MeetWise.Activate");
        if (!first)
        {
            try { _activate.Set(); } catch (Exception) { }
            Environment.Exit(0);
            return;
        }
        _close = new EventWaitHandle(false, EventResetMode.AutoReset, CloseWindowEvent);

        var window = new MainWindow();
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => Dispatcher.BeginInvoke(window.BringToFront), null, Timeout.Infinite, false);
        _closeWait = ThreadPool.RegisterWaitForSingleObject(_close, (_, _) => Dispatcher.BeginInvoke(window.Close), null, Timeout.Infinite, true);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateWait?.Unregister(null);
        _closeWait?.Unregister(null);
        _host?.Dispose();
        // safety net: if a native library keeps a foreground thread alive, the process must still end so the next launch works
        var guard = new Thread(() => { Thread.Sleep(5000); MeetWise.Core.Logging.AppLog.Warn("Process did not exit by itself; forcing exit."); Environment.Exit(0); }) { IsBackground = true };
        guard.Start();
        try { _single?.ReleaseMutex(); } catch (Exception) { }
        base.OnExit(e);
    }
}
