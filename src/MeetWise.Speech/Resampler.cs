namespace MeetWise.Speech;

/// <summary>Converts interleaved float PCM at any rate/channel count to mono 16 kHz for ASR (streaming, stateful).</summary>
public sealed class Resampler
{
    public const int TargetRate = 16000;
    private readonly int _srcRate, _channels;
    private readonly List<float> _pending = new();
    private double _pos;

    public Resampler(int sourceRate, int channels)
    {
        _srcRate = sourceRate;
        _channels = Math.Max(1, channels);
    }

    public float[] Process(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++) sum += interleaved[i * _channels + c];
            _pending.Add(sum / _channels);
        }

        if (_srcRate == TargetRate)
        {
            var all = _pending.ToArray();
            _pending.Clear();
            return all;
        }

        double step = (double)_srcRate / TargetRate;
        var output = new List<float>((int)(_pending.Count / step) + 1);
        // Averaging each step window is a cheap anti-alias filter when decimating.
        while (_pos + step <= _pending.Count)
        {
            int a = (int)_pos;
            int b = Math.Min(_pending.Count, (int)Math.Ceiling(_pos + step));
            float acc = 0;
            for (int k = a; k < b; k++) acc += _pending[k];
            output.Add(acc / Math.Max(1, b - a));
            _pos += step;
        }
        int consumed = (int)_pos;
        _pending.RemoveRange(0, consumed);
        _pos -= consumed;
        return output.ToArray();
    }
}
