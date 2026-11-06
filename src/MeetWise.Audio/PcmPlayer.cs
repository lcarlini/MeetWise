using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetWise.Audio;

/// <summary>Plays synthesized speech on a chosen output device, for example the virtual microphone input, so meeting apps hear the translated voice.</summary>
public sealed class PcmPlayer : IDisposable
{
    private readonly BufferedWaveProvider _buffer;
    private readonly WasapiOut _out;

    public PcmPlayer(MMDevice render, int sampleRate)
    {
        _buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(30),
            DiscardOnBufferOverflow = true
        };
        ISampleProvider chain = _buffer.ToSampleProvider();
        var mix = render.AudioClient.MixFormat;
        if (mix.SampleRate != sampleRate) chain = new WdlResamplingSampleProvider(chain, mix.SampleRate);
        if (mix.Channels == 2) chain = new MonoToStereoSampleProvider(chain);
        _out = new WasapiOut(render, AudioClientShareMode.Shared, true, 60);
        _out.Init(chain);
        _out.Play();
    }

    /// <summary>Seconds of audio still waiting to be played; used to avoid piling up speech that is already stale.</summary>
    public double QueuedSeconds => _buffer.BufferedDuration.TotalSeconds;

    public void Enqueue(float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        _buffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        try { _out.Stop(); } catch { }
        _out.Dispose();
    }
}
