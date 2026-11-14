using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetWise.Audio;

namespace MeetWise.App;

/// <summary>One request from the window to the audio host.</summary>
public sealed record HostRequest(string Cmd);

/// <summary>The host's answer. Only the fields that belong to the command are filled.</summary>
public sealed record HostReply(bool Ok, string? Error = null, MicStatus? Status = null, RouteMeter? Meter = null, CableCheck? Check = null, string? Busy = null);

/// <summary>
/// The window and the audio host talk over a named pipe that only the signed-in user can open. Each request is one JSON line on its own
/// connection, so a crashed or restarted host never leaves the window with a half-open stream.
/// </summary>
public static class HostLink
{
    public const string HostArg = "--audio-host";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static string PipeName => "MeetWise.AudioHost." + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

    public static PipeSecurity CurrentUserOnly()
    {
        var sec = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User!;
        sec.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        return sec;
    }

    /// <summary>Sends one command. Returns null when no host is running (or it did not answer in time).</summary>
    public static async Task<HostReply?> SendAsync(string cmd, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token);
            var line = JsonSerializer.Serialize(new HostRequest(cmd), Json) + "\n";
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(line), cts.Token);
            await pipe.FlushAsync(cts.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            var reply = await reader.ReadLineAsync(cts.Token);
            return reply is null ? null : JsonSerializer.Deserialize<HostReply>(reply, Json);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or JsonException)
        {
            return null;
        }
    }

    public static async Task<bool> IsRunningAsync() => await SendAsync("ping", TimeSpan.FromMilliseconds(400)) is { Ok: true };

    /// <summary>Starts the audio host if it is not running and waits until it answers.</summary>
    public static async Task<bool> EnsureHostAsync()
    {
        if (await IsRunningAsync()) return true;
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, HostArg) { UseShellExecute = false });
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            if (await IsRunningAsync()) return true;
        }
        return false;
    }
}
