using MeetWise.Speech;
using NAudio.Wave;

namespace MeetWise.Tests;

public class SpeechTests
{
    [Fact]
    public void Resampler_48kStereo_To16kMono_HasRightLength()
    {
        var r = new Resampler(48000, 2);
        var block = new float[480 * 2];
        int total = 0;
        for (int i = 0; i < 100; i++) total += r.Process(block).Length;
        Assert.InRange(total, 15900, 16100);
    }

    [Fact]
    public void Resampler_PreservesDcLevel()
    {
        var r = new Resampler(44100, 1);
        var outp = r.Process(Enumerable.Repeat(0.5f, 4410).ToArray());
        Assert.All(outp, v => Assert.Equal(0.5f, v, 3));
    }

    [Fact]
    public void ToFloat_ConvertsInt16()
    {
        var f = Pcm.ToFloat(new byte[] { 0x00, 0x40, 0x00, 0xC0 }, 4, false);
        Assert.Equal(0.5f, f[0], 3);
        Assert.Equal(-0.5f, f[1], 3);
    }
}
