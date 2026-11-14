using System.ComponentModel;
using System.Diagnostics;
using MeetWise.Core.Logging;
using MeetWise.Core.Models;

namespace MeetWise.Audio;

public enum InstallOutcome { Installed, AlreadyInstalled, Cancelled, NeedsReboot, Failed }

public sealed record InstallResult(InstallOutcome Outcome, string Message);

/// <summary>
/// Creates the "MeetWise Microphone" in Windows Sound settings. The microphone itself is a kernel driver, so it comes from VB-Audio's signed
/// VB-CABLE (Secure Boot only loads signed drivers). MeetWise downloads the unmodified pack, runs its setup elevated (one Windows prompt), then
/// renames the two endpoints. The elevated work is done by this same program started with a helper argument.
/// </summary>
public static class VirtualMicInstaller
{
    public const string InstallArg = "--install-virtual-mic";
    public const string UninstallArg = "--uninstall-virtual-mic";
    public const string RenameArg = "--rename-virtual-mic";
    public const string RepairArg = "--repair-virtual-mic";

    public const int ExitOk = 0, ExitSetupMissing = 2, ExitSetupFailed = 3, ExitNotVisible = 4, ExitRenameFailed = 5;
    private const int ErrorCancelled = 1223; // the user declined the Windows administrator prompt

    /// <summary>Maps the helper's exit code and what Windows now shows to a message the user can act on.</summary>
    public static InstallResult Interpret(int exitCode, bool endpointsVisible) => exitCode switch
    {
        ExitOk when endpointsVisible => new(InstallOutcome.Installed, "The MeetWise Microphone is ready. Pick it as the microphone in your meeting apps, or make it the Windows default."),
        ExitOk or ExitNotVisible => new(InstallOutcome.NeedsReboot, "The driver is installed but Windows only shows it after a restart. Restart the PC, then open MeetWise again."),
        ExitSetupMissing => new(InstallOutcome.Failed, "The VB-CABLE setup program was not found. Check your internet connection and try again."),
        ExitSetupFailed => new(InstallOutcome.Failed, "The VB-CABLE setup did not finish. Windows may have blocked the driver; try again and accept the prompt."),
        ExitRenameFailed => new(InstallOutcome.Installed, "The driver works, but Windows would not rename it. It appears as \"CABLE Output\"; pick that as the microphone."),
        _ => new(InstallOutcome.Failed, $"Setup stopped with code {exitCode}.")
    };

    /// <summary>Runs from the normal (not elevated) app.</summary>
    public static async Task<InstallResult> InstallAsync(HttpClient http, IProgress<double>? progress = null,
        Func<string, Task<int>>? runElevated = null, Func<bool>? endpointsVisible = null, CancellationToken ct = default,
        Func<Task>? ensurePack = null, int waitSeconds = 20)
    {
        endpointsVisible ??= VirtualMic.IsInstalled;
        runElevated ??= RunSelfElevatedAsync;
        if (endpointsVisible()) return new(InstallOutcome.AlreadyInstalled, "The MeetWise Microphone is already installed.");

        try { await (ensurePack?.Invoke() ?? VirtualCablePack.EnsureAsync(http, progress, ct: ct)); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            return new(InstallOutcome.Failed, "Could not download the VB-CABLE driver pack: " + ex.Message);
        }

        int code;
        try { code = await runElevated(InstallArg); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled) { return new(InstallOutcome.Cancelled, "Installation cancelled: the Windows administrator prompt was declined."); }

        // new endpoints can take a few seconds to appear after the driver is installed
        for (int i = 0; i < waitSeconds && !endpointsVisible(); i++) await Task.Delay(1000, ct);
        return Interpret(code, endpointsVisible());
    }

    /// <summary>Fixes a cable that lost its playback (or recording) side: restarts the driver, and reinstalls it if that is not enough.</summary>
    public static async Task<InstallResult> RepairAsync(HttpClient? http = null, Func<string, Task<int>>? runElevated = null, Func<bool>? endpointsVisible = null,
        CancellationToken ct = default, Func<Task>? ensurePack = null, int waitSeconds = 20)
    {
        endpointsVisible ??= VirtualMic.IsInstalled;
        runElevated ??= RunSelfElevatedAsync;
        if (endpointsVisible()) return new(InstallOutcome.AlreadyInstalled, "The MeetWise Microphone is already working.");

        // the reinstall fallback needs VB-Audio's setup program on disk; fetching it is harmless when the restart alone is enough
        try { await (ensurePack?.Invoke() ?? (http is null ? Task.CompletedTask : VirtualCablePack.EnsureAsync(http, null, ct: ct))); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException) { AppLog.Warn("Repair: could not fetch the VB-CABLE pack: " + ex.Message); }

        int code;
        try { code = await runElevated(RepairArg); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled) { return new(InstallOutcome.Cancelled, "Repair cancelled: the Windows administrator prompt was declined."); }

        for (int i = 0; i < waitSeconds && !endpointsVisible(); i++) await Task.Delay(1000, ct);
        return Interpret(code, endpointsVisible());
    }

    public static async Task<int> RunSelfElevatedAsync(string argument)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, argument) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the setup helper.");
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    // ---- everything below runs in the elevated helper process ----

    public static int RunInstallHelper()
    {
        var setup = VirtualCablePack.SetupExe();
        if (!File.Exists(setup)) return ExitSetupMissing;
        if (RunSetup(setup, "-i -h") != 0) return ExitSetupFailed;

        for (int i = 0; i < 40 && !VirtualMic.IsInstalled(); i++) Thread.Sleep(1000);
        if (!VirtualMic.IsInstalled()) return ExitNotVisible;
        return RunRenameHelper();
    }

    public static int RunRepairHelper()
    {
        RestartCableDevice();
        for (int i = 0; i < 20 && !VirtualMic.IsInstalled(); i++) Thread.Sleep(1000);
        if (!VirtualMic.IsInstalled())
        {
            // restarting was not enough: run the VB-CABLE setup again, which recreates both endpoints
            var setup = VirtualCablePack.SetupExe();
            if (!File.Exists(setup)) return ExitSetupMissing;
            if (RunSetup(setup, "-i -h") != 0) return ExitSetupFailed;
            for (int i = 0; i < 40 && !VirtualMic.IsInstalled(); i++) Thread.Sleep(1000);
            if (!VirtualMic.IsInstalled()) return ExitNotVisible;
        }
        return RunRenameHelper();
    }

    private static void RestartCableDevice()
    {
        try
        {
            var psi = new ProcessStartInfo("pnputil.exe", "/enum-devices /class MEDIA") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var list = Process.Start(psi);
            if (list is null) return;
            var text = list.StandardOutput.ReadToEnd();
            list.WaitForExit(30000);
            string? id = null, current = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) || line.Contains(": ROOT\\", StringComparison.OrdinalIgnoreCase))
                    current = line[(line.IndexOf("ROOT\\", StringComparison.OrdinalIgnoreCase))..];
                else if (current is not null && line.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)) { id = current; break; }
            }
            if (id is null) { AppLog.Warn("Repair: the VB-Audio device was not found by pnputil."); return; }
            var restart = Process.Start(new ProcessStartInfo("pnputil.exe", $"/restart-device \"{id}\"") { UseShellExecute = false, CreateNoWindow = true });
            restart?.WaitForExit(30000);
            restart?.Dispose();
        }
        catch (Exception ex) { AppLog.Warn("Repair: could not restart the cable driver: " + ex.Message); }
    }

    public static int RunRenameHelper()
    {
        var mic = VirtualMic.FindMic();
        var feed = VirtualMic.FindFeed();
        if (mic is null || feed is null) return ExitNotVisible;
        var errors = new[] { EndpointProperties.WriteDescription(mic.Id, VirtualMic.MicName), EndpointProperties.WriteDescription(feed.Id, VirtualMic.FeedName) }
            .Where(e => e.Length > 0).ToList();
        foreach (var e in errors) AppLog.Warn("Endpoint rename: " + e);
        return errors.Count == 0 ? ExitOk : ExitRenameFailed;
    }

    public static int RunUninstallHelper()
    {
        var setup = VirtualCablePack.SetupExe();
        return File.Exists(setup) ? (RunSetup(setup, "-u -h") == 0 ? ExitOk : ExitSetupFailed) : ExitSetupMissing;
    }

    private static int RunSetup(string setupExe, string args)
    {
        var psi = new ProcessStartInfo(setupExe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(setupExe)! };
        using var p = Process.Start(psi);
        if (p is null) return -1;
        if (!p.WaitForExit(TimeSpan.FromMinutes(3))) { try { p.Kill(true); } catch (InvalidOperationException) { } return -2; }
        return p.ExitCode;
    }

    /// <summary>True when the endpoints exist but still carry VB-Audio's default names (for example after a restart finished the install).</summary>
    public static bool NeedsRename()
    {
        var mic = VirtualMic.FindMic();
        if (mic is null) return false;
        var name = EndpointProperties.ReadDescription(mic.Id);
        return name is not null && !name.Equals(VirtualMic.MicName, StringComparison.OrdinalIgnoreCase);
    }
}
