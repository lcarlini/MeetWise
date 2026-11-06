using System.Threading.Channels;

namespace MeetWise.Speech;

/// <summary>
/// Runs one StreamingRecognizer per audio channel on its own worker, so the audio capture thread
/// only copies and enqueues a buffer and is never blocked by ASR decoding.
/// </summary>
public sealed class LiveTranscriber : IDisposable
{
    private sealed class Lane
    {
        public required StreamingRecognizer Recognizer;
        public required Channel<float[]> Queue;
        public required Task Worker;
        public required bool IsFloat;
    }

    private readonly string _modelDir;
    private readonly Dictionary<string, Lane> _lanes = new();

    public event Action<TranscriptEvent>? Transcript;

    public LiveTranscriber(string modelDir) => _modelDir = modelDir;

    /// <summary>Registers a channel such as "Me" or "Others" with its device format.</summary>
    public void AddChannel(string name, int sampleRate, int channels, bool isFloat32, int threads = 2, string provider = "cpu")
    {
        var rec = new StreamingRecognizer(_modelDir, name, sampleRate, channels, threads, provider);
        rec.Transcript += e => Transcript?.Invoke(e);
        // Bounded queue: if ASR falls behind for a long time we drop the oldest audio instead of growing forever.
        var q = Channel.CreateBounded<float[]>(new BoundedChannelOptions(600)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        var worker = Task.Run(async () =>
        {
            await foreach (var block in q.Reader.ReadAllAsync())
                rec.Feed(block);
            rec.Finish();
        });
        _lanes[name] = new Lane { Recognizer = rec, Queue = q, Worker = worker, IsFloat = isFloat32 };
    }

    /// <summary>Called from the audio thread: converts and enqueues, never decodes.</summary>
    public void Push(string channel, byte[] buffer, int count)
    {
        if (!_lanes.TryGetValue(channel, out var lane)) return;
        lane.Queue.Writer.TryWrite(ToFloat(buffer, count, lane.IsFloat));
    }

    public static float[] ToFloat(byte[] buffer, int count, bool isFloat32)
    {
        if (isFloat32)
        {
            var f = new float[count / 4];
            Buffer.BlockCopy(buffer, 0, f, 0, f.Length * 4);
            return f;
        }
        var s = new float[count / 2];
        for (int i = 0; i < s.Length; i++)
            s[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
        return s;
    }

    /// <summary>Drains queues, flushes the last utterance of each channel and stops the workers.</summary>
    public void Stop()
    {
        foreach (var lane in _lanes.Values) lane.Queue.Writer.TryComplete();
        Task.WaitAll(_lanes.Values.Select(l => l.Worker).ToArray(), TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        Stop();
        foreach (var lane in _lanes.Values) lane.Recognizer.Dispose();
        _lanes.Clear();
    }
}
