using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetWise.Audio;

/// <summary>
/// The far-end reference on the same clock as the microphone. One thread writes what the speakers played (the loopback callback), another reads
/// the block that belongs to the microphone block it is processing. Positions are sample counts, so there is no queue to back up:
/// <list type="bullet">
/// <item>WASAPI loopback delivers nothing while the speakers are silent. Late data is therefore placed at its wall-clock position and the gap is
/// filled with zeros, so the reference never slides against the microphone when playback starts and stops.</item>
/// <item>The reader sits a small margin behind the writer so jitter in packet arrival cannot starve it.</item>
/// <item>It is a lock-free single-producer / single-consumer ring: no locks and no allocation on either side.</item>
/// </list>
/// The remaining offset (device latency, margin) is measured by the echo canceller's delay estimator, never assumed.
/// </summary>
public sealed class ReferenceTimeline
{
    private readonly float[] _buf;
    private readonly int _rate;
    private readonly Func<long> _clock;
    private readonly long _ticksPerSecond;
    private readonly int _marginSamples, _gapSamples;
    private long _t0, _written, _readPos;
    private long _gapFilled, _underruns;

    public int SampleRate => _rate;

    /// <summary>Samples of silence inserted to keep the reference on wall-clock time.</summary>
    public long GapSamplesFilled => Interlocked.Read(ref _gapFilled);

    /// <summary>Reads that found the reference not yet written (the part that was missing is zeros).</summary>
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>How far the writer is ahead of the reader, in samples (diagnostics).</summary>
    public long Lag => Volatile.Read(ref _written) - Volatile.Read(ref _readPos);

    /// <param name="clock">Monotonic ticks; defaults to the high-resolution timer. Injected so tests control time.</param>
    public ReferenceTimeline(int sampleRate, int capacitySeconds = 4, Func<long>? clock = null, long? ticksPerSecond = null, int marginMs = 60, int gapToleranceMs = 30)
    {
        _rate = sampleRate;
        _buf = new float[sampleRate * capacitySeconds];
        _clock = clock ?? Stopwatch.GetTimestamp;
        _ticksPerSecond = ticksPerSecond ?? Stopwatch.Frequency;
        _marginSamples = sampleRate * marginMs / 1000;
        _gapSamples = sampleRate * gapToleranceMs / 1000;
        Start();
    }

    /// <summary>(Re)starts the timeline now. Everything written before is forgotten, so a restarted stream can never play stale audio.</summary>
    public void Start()
    {
        _t0 = _clock();
        Volatile.Write(ref _written, 0);
        Volatile.Write(ref _readPos, -_marginSamples);
        Array.Clear(_buf);
    }

    /// <summary>Appends reference samples (already at <see cref="SampleRate"/>). Call from the producer thread only.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        long written = Volatile.Read(ref _written);
        long expected = (long)((_clock() - _t0) * (double)_rate / _ticksPerSecond);
        long end = written + samples.Length;
        if (end + _gapSamples < expected)
        {
            // nothing arrived for a while (silent speakers): the data belongs at the present, not right after the old data
            long pad = Math.Min(expected - samples.Length - written, _buf.Length);
            if (pad > 0)
            {
                for (long i = 0; i < pad; i++) _buf[(written + i) % _buf.Length] = 0f;
                written += pad;
                Interlocked.Add(ref _gapFilled, pad);
            }
        }
        for (int i = 0; i < samples.Length; i++) _buf[(written + i) % _buf.Length] = samples[i];
        Volatile.Write(ref _written, written + samples.Length);
    }

    /// <summary>Fills <paramref name="dest"/> with the reference that belongs to the next microphone block. Call from the consumer thread only.</summary>
    public void Read(Span<float> dest)
    {
        long written = Volatile.Read(ref _written);
        long pos = Volatile.Read(ref _readPos);
        bool starved = false;
        for (int i = 0; i < dest.Length; i++)
        {
            long idx = pos + i;
            if (idx < 0) { dest[i] = 0f; continue; }
            if (idx >= written) { dest[i] = 0f; starved = true; continue; }
            if (idx < written - _buf.Length) { dest[i] = 0f; continue; } // overwritten: too old to be meaningful
            dest[i] = _buf[idx % _buf.Length];
        }
        if (starved) Interlocked.Increment(ref _underruns);
        // If the writer has run far ahead (its clock is faster), jump forward instead of playing ever older audio.
        long next = pos + dest.Length;
        long limit = written - _buf.Length / 2;
        Volatile.Write(ref _readPos, next < limit ? limit : next);
    }
}

/// <summary>Streaming sample-rate converter for the reference (mono float). Uses NAudio's WDL resampler; a pass-through when the rates match.</summary>
public sealed class ReferenceResampler
{
    private readonly BufferedWaveProvider? _in;
    private readonly ISampleProvider? _chain;
    private readonly float[] _scratch = new float[4096];
    private byte[] _bytes = new byte[0];

    public int InputRate { get; }
    public int OutputRate { get; }

    public ReferenceResampler(int inputRate, int outputRate)
    {
        InputRate = inputRate;
        OutputRate = outputRate;
        if (inputRate == outputRate) return;
        _in = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(inputRate, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };
        _chain = new WdlResamplingSampleProvider(_in.ToSampleProvider(), outputRate);
    }

    /// <summary>Converts one block; calls <paramref name="sink"/> with every chunk of output produced.</summary>
    public void Process(ReadOnlySpan<float> input, ReferenceSink sink)
    {
        if (_in is null) { sink(input); return; }
        int need = input.Length * 4;
        if (_bytes.Length < need) _bytes = new byte[need];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(input).CopyTo(_bytes);
        _in.AddSamples(_bytes, 0, need);
        int n;
        while ((n = _chain!.Read(_scratch, 0, _scratch.Length)) > 0) sink(_scratch.AsSpan(0, n));
    }
}

public delegate void ReferenceSink(ReadOnlySpan<float> samples);
