using System.Threading.Channels;
using SherpaOnnx;

namespace MeetWise.Speech;

public sealed record LiveSubtitlerOptions(
    string Language,                 // "auto" or an ISO code such as "en", "pt", "zh"
    bool TranslateToEnglish,
    string VadModel,
    string? WhisperModel,            // ggml file; required unless the streaming English path is used
    string? EnglishStreamingDir,     // streaming zipformer folder; used for plain English (words appear while speaking)
    Func<string, string>? Punctuate = null,
    IReadOnlyList<string>? Vocabulary = null,
    int Threads = 2);

/// <summary>
/// Real-time subtitles for any language. English without translation uses the streaming recognizer (words appear as they are spoken, with partial
/// results). Every other case cuts speech into short phrases with a voice-activity detector (at most 8 s each) and transcribes each phrase with
/// Whisper on the GPU, so a subtitle appears about a second after the phrase ends. Capture threads only enqueue audio and never wait for the models.
/// </summary>
public sealed class LiveSubtitler : IDisposable
{
    public const double MaxPhraseSeconds = 8.0;

    private readonly LiveSubtitlerOptions _o;
    private readonly bool _streamingEnglish;
    private readonly LiveTranscriber? _english;
    private readonly WhisperTranscriber? _whisper;
    private readonly Dictionary<string, PhraseLane> _lanes = new();
    private readonly Channel<(string Channel, double Start, float[] Samples)> _phrases =
        Channel.CreateBounded<(string, double, float[])>(new BoundedChannelOptions(24) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private Task? _whisperWorker;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>A line that is still being spoken (streaming English only).</summary>
    public event Action<string, string>? Partial;

    /// <summary>A finished subtitle.</summary>
    public event Action<SubtitleCue>? Cue;

    /// <summary>True while a channel's speech detector hears speech (lets the UI show "speaking…" for the Whisper path).</summary>
    public event Action<string, bool>? Speaking;

    public bool UsesStreamingEnglish => _streamingEnglish;

    public LiveSubtitler(LiveSubtitlerOptions options)
    {
        _o = options;
        _streamingEnglish = options.Language == "en" && !options.TranslateToEnglish && options.EnglishStreamingDir is not null;
        if (_streamingEnglish)
        {
            _english = new LiveTranscriber(options.EnglishStreamingDir!);
            _english.Transcript += OnEnglish;
        }
        else
        {
            if (options.WhisperModel is null) throw new ArgumentException("A Whisper model is required for this language.", nameof(options));
            _whisper = new WhisperTranscriber(options.WhisperModel);
            _whisperWorker = Task.Run(WhisperLoopAsync);
        }
    }

    public void AddChannel(string name, int sampleRate, int channels, bool isFloat32)
    {
        if (_english is not null) _english.AddChannel(name, sampleRate, channels, isFloat32, _o.Threads);
        else _lanes[name] = new PhraseLane(name, sampleRate, channels, isFloat32, _o, this);
    }

    /// <summary>Called from the audio thread: converts and enqueues only.</summary>
    public void Push(string channel, byte[] buffer, int count)
    {
        if (_english is not null) _english.Push(channel, buffer, count);
        else if (_lanes.TryGetValue(channel, out var lane)) lane.Enqueue(LiveTranscriber.ToFloat(buffer, count, lane.IsFloat));
    }

    private void OnEnglish(TranscriptEvent e)
    {
        if (!e.IsFinal) { Partial?.Invoke(e.Channel, e.Text); return; }
        var text = _o.Punctuate?.Invoke(e.Text) ?? e.Text;
        Cue?.Invoke(new SubtitleCue(e.StartSec, e.EndSec, text, e.Channel, "en"));
    }

    private async Task WhisperLoopAsync()
    {
        try
        {
            await foreach (var (channel, start, samples) in _phrases.Reader.ReadAllAsync(_cts.Token))
            {
                if (IsNearSilent(samples)) continue; // Whisper invents text for silence
                IReadOnlyList<WhisperSegment> segs;
                try
                {
                    segs = await _whisper!.TranscribeAsync(samples, _o.Language, _o.Vocabulary, _o.Threads, null, _cts.Token, _o.TranslateToEnglish);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { continue; } // one bad phrase must not stop the subtitles
                foreach (var s in segs)
                {
                    if (IsNoiseText(s.Text)) continue;
                    Cue?.Invoke(new SubtitleCue(start + s.Start, start + Math.Max(s.End, s.Start + 0.3), s.Text, channel, _o.TranslateToEnglish ? "en" : s.Language));
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal void Emit(string channel, double start, float[] samples) => _phrases.Writer.TryWrite((channel, start, samples));
    internal void RaiseSpeaking(string channel, bool speaking) => Speaking?.Invoke(channel, speaking);

    public static bool IsNearSilent(float[] samples)
    {
        if (samples.Length == 0) return true;
        double sum = 0;
        foreach (var v in samples) sum += v * v;
        return Math.Sqrt(sum / samples.Length) < 0.002;
    }

    /// <summary>Whisper's usual hallucinations and non-speech markers.</summary>
    public static bool IsNoiseText(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return true;
        if (t.All(c => !char.IsLetterOrDigit(c))) return true;                       // only punctuation or symbols
        if ((t.StartsWith('[') && t.EndsWith(']')) || (t.StartsWith('(') && t.EndsWith(')'))) return true; // [BLANK_AUDIO], (music)
        return t.Equals("you", StringComparison.OrdinalIgnoreCase); // Whisper's classic answer to near-silence
    }

    /// <summary>Flushes the last phrase of every channel and waits for the models to finish what is queued.</summary>
    public void Stop()
    {
        _english?.Stop();
        foreach (var lane in _lanes.Values) lane.Finish();
        _phrases.Writer.TryComplete();
        try { _whisperWorker?.Wait(TimeSpan.FromSeconds(60)); } catch (AggregateException) { }
    }

    public void Dispose()
    {
        Stop();
        _cts.Cancel();
        _english?.Dispose();
        foreach (var lane in _lanes.Values) lane.Dispose();
        _whisper?.Dispose();
        _cts.Dispose();
    }

    /// <summary>One audio channel: resample to 16 kHz mono, detect phrases, hand each phrase to the shared Whisper worker.</summary>
    private sealed class PhraseLane : IDisposable
    {
        private const int Window = 512;
        private readonly string _name;
        private readonly Resampler _resampler;
        private readonly VoiceActivityDetector _vad;
        private readonly Channel<float[]?> _input = Channel.CreateBounded<float[]?>(new BoundedChannelOptions(600) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private readonly LiveSubtitler _owner;
        private readonly Task _worker;
        private readonly List<float> _pending = new();
        private bool _speaking;

        public bool IsFloat { get; }

        public PhraseLane(string name, int rate, int channels, bool isFloat, LiveSubtitlerOptions o, LiveSubtitler owner)
        {
            _name = name;
            _owner = owner;
            IsFloat = isFloat;
            _resampler = new Resampler(rate, channels);
            var cfg = new VadModelConfig();
            cfg.SileroVad.Model = o.VadModel;
            cfg.SileroVad.Threshold = 0.5f;
            cfg.SileroVad.MinSilenceDuration = 0.35f;
            cfg.SileroVad.MinSpeechDuration = 0.25f;
            cfg.SileroVad.MaxSpeechDuration = (float)MaxPhraseSeconds;
            cfg.SileroVad.WindowSize = Window;
            cfg.SampleRate = 16000;
            cfg.NumThreads = 1;
            _vad = new VoiceActivityDetector(cfg, 60);
            _worker = Task.Run(RunAsync);
        }

        public void Enqueue(float[] block) => _input.Writer.TryWrite(block);

        public void Finish() { _input.Writer.TryComplete(); try { _worker.Wait(TimeSpan.FromSeconds(30)); } catch (AggregateException) { } }

        private async Task RunAsync()
        {
            await foreach (var block in _input.Reader.ReadAllAsync())
            {
                if (block is null) continue;
                _pending.AddRange(_resampler.Process(block));
                int used = 0;
                while (_pending.Count - used >= Window)
                {
                    _vad.AcceptWaveform(_pending.GetRange(used, Window).ToArray());
                    used += Window;
                    Drain();
                    bool now = _vad.IsSpeechDetected();
                    if (now != _speaking) { _speaking = now; _owner.RaiseSpeaking(_name, now); }
                }
                _pending.RemoveRange(0, used);
            }
            _vad.Flush();
            Drain();
            if (_speaking) { _speaking = false; _owner.RaiseSpeaking(_name, false); }
        }

        private void Drain()
        {
            while (!_vad.IsEmpty())
            {
                var seg = _vad.Front();
                _owner.Emit(_name, seg.Start / 16000.0, seg.Samples);
                _vad.Pop();
            }
        }

        public void Dispose() => _vad.Dispose();
    }
}
