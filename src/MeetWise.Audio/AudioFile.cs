using MeetWise.Speech;
using NAudio.Wave;

namespace MeetWise.Audio;

/// <summary>Reads any audio file Windows can decode as mono float samples.</summary>
public static class AudioFile
{
    public static float[] ReadMono16k(string path)
    {
        using var reader = new MediaFoundationReader(path);
        var samples = reader.ToSampleProvider();
        var fmt = samples.WaveFormat;
        var rs = new Resampler(fmt.SampleRate, fmt.Channels);
        var all = new List<float>();
        var buf = new float[fmt.SampleRate * fmt.Channels];
        int n;
        while ((n = samples.Read(buf, 0, buf.Length)) > 0)
            all.AddRange(rs.Process(buf.AsSpan(0, n)));
        return all.ToArray();
    }

    /// <summary>Mono at the file's own sample rate (channels averaged).</summary>
    public static (float[] Samples, int Rate) ReadMono(string path)
    {
        using var reader = new AudioFileReader(path);
        var fmt = reader.WaveFormat;
        var all = new List<float>();
        var buf = new float[fmt.SampleRate * fmt.Channels];
        int n;
        while ((n = reader.Read(buf, 0, buf.Length)) > 0)
            all.AddRange(AudioRoute.ToMono(buf[..n], fmt.Channels));
        return (all.ToArray(), fmt.SampleRate);
    }

    public static void WriteWav(string path, float[] mono, int rate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
        w.WriteSamples(mono, 0, mono.Length);
    }
}
