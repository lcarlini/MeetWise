using System.Windows;

namespace MeetWise.App;

public partial class App : Application
{
    private Mutex? _single;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            // Headless check of every native component, used to validate an installed or published build on any PC.
            var path = e.Args.SkipWhile(a => !a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
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
        new MainWindow().Show();
    }
}
