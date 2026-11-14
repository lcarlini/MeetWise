using System.IO;
using System.Net.Http;
using MeetWise.Audio;
using MeetWise.Core;
using MeetWise.Core.Hardware;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;
using MeetWise.Speech;

namespace MeetWise.App;

/// <summary>
/// Headless check of the clean microphone on this PC (<c>MeetWise.App.exe --selftest</c>): devices, the virtual cable, every noise model, the
/// full chain's speed, and, when the background microphone is running, the end-to-end cable check (a test tone written into the MeetWise
/// Mic Feed must come back out of the MeetWise Microphone). Exit code 0 means every required check passed. The report is also written to
/// %LocalAppData%\MeetWise\selftest.txt.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        var lines = new List<string>();
        int failures = 0;

        async Task Check(string name, Func<Task<string>> body, bool required = true)
        {
            try { lines.Add($"PASS  {name}: {await body()}"); }
            catch (Exception ex)
            {
                lines.Add($"{(required ? "FAIL" : "WARN")}  {name}: {ex.GetType().Name}: {ex.Message}");
                if (required) failures++;
            }
        }

        lines.Add($"MeetWise self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss} on {Environment.OSVersion}");
        await Check("Hardware", () =>
        {
            var hw = HardwareDetector.Detect();
            return Task.FromResult($"tier {hw.Tier}, {hw.CpuThreads} threads, GPU {(hw.Gpus.Count > 0 ? hw.Gpus[0].Name + " " + hw.Gpus[0].VramMb + " MB" : "none")}");
        });
        await Check("Audio devices (WASAPI)", () =>
        {
            var svc = new AudioDeviceService();
            return Task.FromResult($"{svc.GetMicrophones().Count - 1} physical microphone(s), {svc.GetRenderDevices().Count} output(s)");
        });
        await Check("Virtual cable", () =>
        {
            var state = VirtualMic.GetState();
            var f = VirtualMic.Formats();
            var disabled = VirtualMic.DisabledEndpoints();
            var text = $"{state}; mic '{VirtualMic.FindMic()?.Name}', feed '{VirtualMic.FindFeed()?.Name}'"
                + (f is { } x ? $"; feed {x.FeedRate} Hz/{x.FeedChannels} ch, mic {x.MicRate} Hz/{x.MicChannels} ch" : "")
                + (disabled.Count > 0 ? $"; disabled: {string.Join(", ", disabled.Select(d => d.Name))}" : "");
            return state == CableState.Ready ? Task.FromResult(text) : throw new InvalidOperationException(text);
        });

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var manager = new ModelManager(http);
        foreach (var (model, kind) in new[] { (ModelCatalog.DenoiseLight, EnhancerKind.Light), (ModelCatalog.DenoiseBalanced, EnhancerKind.FullBand), (ModelCatalog.DenoiseFullBand, EnhancerKind.FullBand) })
        {
            await Check($"Noise model {model.Id}", async () =>
            {
                await manager.EnsureAsync(model);
                var path = manager.PathFor(model);
                int? lat = LatencyProbe.Measure(kind, path);
                using var e = new SpeechEnhancer(kind, path);
                return $"{e.OutputSampleRate} Hz, look-ahead {(lat is { } l ? (l * 1000.0 / e.OutputSampleRate).ToString("0.0") + " ms" : "unknown")}";
            });
        }

        await Check("Full chain speed (Max + echo + auto gain, 10 s at 48 kHz in 10 ms blocks)", async () =>
        {
            var s = new AppSettings { NoiseCancellation = NoiseMode.Max, EchoCancellation = true, AutoGain = true };
            var options = await MicService.OptionsForAsync(http, s, NoiseMode.Max, false, null);
            using var engine = new EnhancementEngine(48000, options);
            var voice = LatencyProbe.SyntheticVoice(48000, 10);
            var far = new float[480];
            var rng = new Random(1);
            for (int i = 0; i + 480 <= voice.Length; i += 480)
            {
                for (int k = 0; k < 480; k++) far[k] = (float)(rng.NextDouble() - 0.5) * 0.1f;
                engine.Process(voice[i..(i + 480)], far);
            }
            var rtf = engine.RealTimeFactor;
            var text = $"real-time factor {rtf:0.000} ({rtf:P1} of one core), model look-ahead {engine.ModelLatencyMs:0.0} ms, errors {engine.Errors}";
            return rtf < 0.5 ? text : throw new InvalidOperationException("too slow: " + text);
        });

        await Check("Background microphone (audio host)", async () =>
        {
            var reply = await HostLink.SendAsync("status", TimeSpan.FromSeconds(2)) ?? throw new InvalidOperationException("not running");
            var st = reply.Status!;
            return $"{(st.Running ? "running" : "off")} ({st.State}), applied: {st.Applied}{(reply.Error is null ? "" : ", error: " + reply.Error)}";
        }, required: false);

        await Check("End-to-end: test tone through the MeetWise Microphone", async () =>
        {
            var reply = await HostLink.SendAsync("check", TimeSpan.FromSeconds(10)) ?? throw new InvalidOperationException("background microphone not running");
            var c = reply.Check!;
            return c.Passed ? $"{c.Message} (delay {c.DelayMs:0} ms)" : throw new InvalidOperationException(c.Message);
        }, required: false);

        lines.Add(failures == 0 ? "RESULT: all required checks passed" : $"RESULT: {failures} required check(s) failed");
        var report = string.Join(Environment.NewLine, lines);
        Console.WriteLine(report);
        try
        {
            var path = Path.Combine(AppPaths.Local, "selftest.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, report);
        }
        catch (Exception) { }
        return failures == 0 ? 0 : 1;
    }
}
