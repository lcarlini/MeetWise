using System.Windows;
using MeetWise.Audio;

namespace MeetWise.App;

public partial class App : Application
{
    private Mutex? _single;
    private EventWaitHandle? _activate;
    private RegisteredWaitHandle? _activateWait;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var args = e.Args;
        bool Has(string a) => args.Contains(a, StringComparer.OrdinalIgnoreCase);

        // Elevated helper modes: the normal app starts itself with administrator rights for exactly one job and exits.
        if (Has(VirtualMicInstaller.InstallArg)) { Environment.Exit(VirtualMicInstaller.RunInstallHelper()); return; }
        if (Has(VirtualMicInstaller.UninstallArg)) { Environment.Exit(VirtualMicInstaller.RunUninstallHelper()); return; }
        if (Has(VirtualMicInstaller.RenameArg)) { Environment.Exit(VirtualMicInstaller.RunRenameHelper()); return; }

        if (Has("--selftest"))
        {
            // Headless check of every native component, used to validate an installed or published build on any PC.
            var path = args.SkipWhile(a => !a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
            Environment.Exit(SelfTest.Run(path));
            return;
        }

        if (Has("--benchmark"))
        {
            // Headless quality and speed report of every feature on a test clip: --benchmark [clip.wav] [--quick] [--out report.md]
            var rest = args.SkipWhile(a => !a.Equals("--benchmark", StringComparison.OrdinalIgnoreCase)).Skip(1).ToList();
            string? Value(string flag) { int i = rest.FindIndex(x => x.Equals(flag, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < rest.Count ? rest[i + 1] : null; }
            var clip = rest.FirstOrDefault(x => !x.StartsWith("--") && !x.Equals(Value("--out"), StringComparison.OrdinalIgnoreCase) && !x.Equals(Value("--dump"), StringComparison.OrdinalIgnoreCase));
            Environment.Exit(Task.Run(() => Benchmark.RunAsync(clip, Has("--quick"), Value("--out"), Has("--tune"), Value("--dump"))).GetAwaiter().GetResult());
            return;
        }

        // One instance only: a second launch would fight over the audio devices and the database.
        _single = new Mutex(true, @"Local\MeetWise.SingleInstance", out bool first);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MeetWise.Activate");
        if (!first)
        {
            // the running copy may be hidden in the tray: ask it to show its window instead of refusing to start
            try { _activate.Set(); } catch (Exception) { }
            Environment.Exit(0);
            return;
        }

        var window = new MainWindow();
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => Dispatcher.BeginInvoke(() => window.RestoreFromTray()), null, Timeout.Infinite, false);
        window.Show();
        // started by Windows at sign-in: stay in the tray so the MeetWise Microphone works without a window in the way
        if (Has("--minimized")) window.WindowState = WindowState.Minimized;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateWait?.Unregister(null);
        // safety net: if a native library keeps a foreground thread alive, the process must still end so the next launch works
        var guard = new Thread(() => { Thread.Sleep(5000); MeetWise.Core.Logging.AppLog.Warn("Process did not exit by itself; forcing exit."); Environment.Exit(0); }) { IsBackground = true };
        guard.Start();
        try { _single?.ReleaseMutex(); } catch (Exception) { }
        base.OnExit(e);
    }
}
