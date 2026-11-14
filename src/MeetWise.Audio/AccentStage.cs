using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MeetWise.Core.Logging;
using MeetWise.Core.Models;
using MeetWise.Speech;

namespace MeetWise.Audio;

/// <summary>
/// Accent Assist in the live microphone: the cleaned voice goes to the accent worker (Python on the GPU, see accent\worker.py) and comes back
/// softened. The output runs at a <b>fixed</b> delay: when the converted audio for a moment is not back in time (worker busy, starting or
/// gone), the original voice for that moment is used instead, with 10 ms cross-fades, so the call is never silent and the timing never jumps.
/// The worker runs at 16 kHz; the stage converts to and from the microphone chain's rate.
/// </summary>
public sealed class AccentStage : IDisposable
{
    private const int WorkerRate = 16000;
    private readonly int _rate;
    private readonly float _strength;
    private readonly string _bank;
    private readonly int _requestedDelay;
    private readonly float[] _dry, _wet;
    private long _inCount, _wetCount;
    private readonly object _wetLock = new();
    private readonly StreamResampler _down;
    private readonly BlockingCollection<float[]> _send = new(new ConcurrentQueue<float[]>(), 400);
    private readonly CancellationTokenSource _cts = new();
    private System.Diagnostics.Process? _worker;
    private Thread? _io;
    private int _delay;             // output delay in samples at _rate (fixed once the worker is connected)
    private int _wetLag;            // how far behind the input the converted stream is (worker latency + resampling)
    private float _mix;             // 0 = original voice, 1 = converted
    private readonly float _mixStep;
    private volatile bool _connected;
    private long _fallbackSamples, _outSamples;

    /// <summary>"starting", "on", "fallback" (worker late or gone) or an error.</summary>
    public string State { get; private set; } = "starting";

    /// <summary>The delay the accent stage adds, in milliseconds.</summary>
    public double DelayMs => _delay * 1000.0 / _rate;

    /// <summary>Share of the output that had to use the original voice because the converted audio was late.</summary>
    public double FallbackShare => _outSamples == 0 ? 0 : _fallbackSamples / (double)_outSamples;

    public AccentStage(int rate, int bufferMs, float strength, string bank)
    {
        _rate = rate;
        _strength = Math.Clamp(strength, 0f, 1f);
        _bank = bank;
        _requestedDelay = rate * Math.Clamp(bufferMs, 80, 600) / 1000;
        _delay = _requestedDelay;
        _dry = new float[rate * 2];
        _wet = new float[rate * 2];
        _down = new StreamResampler(rate, WorkerRate);
        _mixStep = 1f / (rate / 100f);
    }

    /// <summary>Starts the worker in the background; until it answers, the original voice passes (delayed by the buffer).</summary>
    public void Start() => _io = new Thread(Run) { IsBackground = true, Name = "MeetWise accent worker link" }.Also(t => t.Start());

    /// <summary>Offline use (bench): wait for the converted audio instead of falling back to the original voice.</summary>
    public bool Synchronous { get; set; }

    /// <summary>Waits until the worker is connected (or failed). Returns true when connected.</summary>
    public bool WaitConnected(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!_connected && sw.Elapsed < timeout && (State == "starting")) Thread.Sleep(50);
        return _connected;
    }

    /// <summary>Called on the audio thread with the cleaned voice; returns the same number of samples.</summary>
    public float[] Process(float[] block)
    {
        if (block.Length == 0) return block;
        if (Synchronous && _connected)
        {
            // make sure the converted audio for this whole block has arrived
            long need = _inCount + block.Length - _delay + _wetLag;
            _send.TryAdd(_down.Process(block));
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < block.Length; i++) _dry[(_inCount + i) % _dry.Length] = block[i];
            _inCount += block.Length;
            while (sw.Elapsed < TimeSpan.FromSeconds(5)) { lock (_wetLock) if (_wetCount >= need) break; Thread.Sleep(1); }
            return Output(block.Length);
        }
        for (int i = 0; i < block.Length; i++) _dry[(_inCount + i) % _dry.Length] = block[i];
        _inCount += block.Length;
        if (_connected) _send.TryAdd(_down.Process(block));
        return Output(block.Length);
    }


    private float[] Output(int count)
    {
        var y = new float[count];
        long firstOut = _inCount - count - _delay;
        long wetAvailable;
        lock (_wetLock) wetAvailable = _wetCount;
        for (int i = 0; i < y.Length; i++)
        {
            long o = firstOut + i;
            if (o < 0) { y[i] = 0; continue; }
            float dry = _dry[o % _dry.Length];
            long w = o + _wetLag;
            bool have = _connected && w < wetAvailable && w >= wetAvailable - _wet.Length;
            _mix = have ? Math.Min(1f, _mix + _mixStep) : Math.Max(0f, _mix - _mixStep);
            float wet = have ? _wet[w % _wet.Length] : 0f;
            y[i] = _mix * wet + (1 - _mix) * dry;
            if (!have) _fallbackSamples++;
        }
        _outSamples += y.Length;
        if (_connected) State = FallbackShare > 0.2 && _outSamples > _rate * 5 ? "fallback" : "on";
        return y;
    }

    private void Run()
    {
        try
        {
            var script = AccentPack.WorkerScript ?? throw new FileNotFoundException("accent\\worker.py not found");
            // the worker needs about 0.7 GB of graphics memory; NVIDIA Broadcast and other apps share the card
            if (FreeGpuMemoryMb() is { } free)
            {
                AppLog.Info($"Accent Assist: {free} MB of graphics memory free.");
                if (free < MinFreeGpuMb) throw new InvalidOperationException($"only {free} MB of graphics memory is free (needs {MinFreeGpuMb} MB)");
            }
            var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            var psi = new ProcessStartInfo(AccentPack.Python, $"\"{script}\" --port 0 --token {token}")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(script)!
            };
            _worker = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("the accent worker did not start");
            _worker.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } d && !d.Contains("Warning", StringComparison.OrdinalIgnoreCase)) AppLog.Info("accent worker: " + d); };
            _worker.BeginErrorReadLine();
            int port = 0;
            var sw = Stopwatch.StartNew();
            while (port == 0 && sw.Elapsed < TimeSpan.FromSeconds(120) && !_cts.IsCancellationRequested)
            {
                var line = _worker.StandardOutput.ReadLine() ?? throw new InvalidOperationException("the accent worker exited while loading");
                if (line.StartsWith("PORT ")) port = int.Parse(line[5..]);
            }
            if (port == 0) throw new TimeoutException("the accent worker did not load in time");

            using var client = new TcpClient { NoDelay = true };
            client.Connect("127.0.0.1", port);
            using var stream = client.GetStream();
            var hello = JsonSerializer.Serialize(new { token, bank = _bank, strength = _strength }) + "\n";
            stream.Write(Encoding.UTF8.GetBytes(hello));
            var answer = ReadLine(stream);
            using var doc = JsonDocument.Parse(answer);
            if (!doc.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(doc.RootElement.GetProperty("error").GetString());
            int workerLatency = doc.RootElement.GetProperty("latency").GetInt32();
            // what comes back lags the input by the worker's latency plus the two resampling filters (about 2 ms)
            _wetLag = (int)((long)workerLatency * _rate / WorkerRate) + _rate / 500;
            // never ask for less delay than the converted audio needs, plus 60 ms for compute and transport
            _delay = Math.Max(_requestedDelay, _wetLag + _rate * 60 / 1000);
            var up = new StreamResampler(WorkerRate, _rate);
            AppLog.Info($"Accent Assist on: worker latency {workerLatency * 1000.0 / WorkerRate:0} ms, output delay {DelayMs:0} ms, bank {Path.GetFileName(_bank)}.");
            _connected = true;

            var header = new byte[4];
            while (!_cts.IsCancellationRequested)
            {
                // send everything queued as one frame
                var first = _send.Take(_cts.Token);
                var parts = new List<float[]> { first };
                while (_send.TryTake(out var more)) parts.Add(more);
                int n = parts.Sum(p => p.Length);
                var payload = new byte[4 + 4 * n];
                BitConverter.TryWriteBytes(payload.AsSpan(0, 4), n);
                int off = 4;
                foreach (var p in parts) { Buffer.BlockCopy(p, 0, payload, off, p.Length * 4); off += p.Length * 4; }
                stream.Write(payload);

                ReadExact(stream, header);
                int m = BitConverter.ToInt32(header);
                var body = new byte[4 * m];
                ReadExact(stream, body);
                var y16 = new float[m];
                Buffer.BlockCopy(body, 0, y16, 0, body.Length);
                var y = up.Process(y16);
                lock (_wetLock)
                {
                    for (int i = 0; i < y.Length; i++) _wet[(_wetCount + i) % _wet.Length] = y[i];
                    _wetCount += y.Length;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            State = "error: " + ex.Message;
            AppLog.Warn("Accent Assist stopped, the original voice passes: " + ex.Message);
        }
        finally { _connected = false; }
    }

    public const int MinFreeGpuMb = 1500;

    /// <summary>Free memory of the first NVIDIA GPU from nvidia-smi, or null when it cannot be read.</summary>
    public static int? FreeGpuMemoryMb()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.free --format=csv,noheader,nounits")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return int.TryParse(text.Split('\n')[0].Trim(), out var mb) ? mb : null;
        }
        catch (Exception) { return null; }
    }

    private static string ReadLine(NetworkStream s)
    {
        var sb = new List<byte>();
        int b;
        while ((b = s.ReadByte()) >= 0 && b != '\n') sb.Add((byte)b);
        return Encoding.UTF8.GetString(sb.ToArray());
    }

    private static void ReadExact(NetworkStream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0) throw new IOException("the accent worker closed the connection");
            got += n;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _connected = false;
        try { if (_worker is { HasExited: false }) _worker.Kill(entireProcessTree: true); } catch (Exception) { }
        _worker?.Dispose();
    }
}

internal static class ObjectExtensions
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
