using System.Windows;
using MeetWise.Audio;

namespace MeetWise.App;

public partial class App : Application
{
    private Mutex? _single;

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

        // One instance only: a second launch would fight over the audio devices and the database.
        _single = new Mutex(true, @"Local\MeetWise.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("MeetWise is already running. Look for it in the system tray.", "MeetWise");
            Shutdown();
            return;
        }

        var window = new MainWindow();
        window.Show();
        // started by Windows at sign-in: stay in the tray so the MeetWise Microphone works without a window in the way
        if (Has("--minimized")) window.WindowState = WindowState.Minimized;
    }
}
