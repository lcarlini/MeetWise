using System.Runtime.InteropServices;
using MeetWise.Audio;

namespace MeetWise.Tests;

public class AudioErrorsTests
{
    [Theory]
    [InlineData(0x80040154u, "not responding")]
    [InlineData(0x88890004u, "not responding")]
    [InlineData(0x80070490u, "not responding")]
    [InlineData(0x8889000Au, "exclusive mode")]
    [InlineData(0x800706CCu, "twice")]
    public void KnownAudioErrors_BecomeActionableSentences(uint hr, string expected)
    {
        var ex = new COMException("raw text", unchecked((int)hr));
        var text = AudioErrors.Describe(ex);
        Assert.Contains(expected, text);
        Assert.DoesNotContain("0x", text);
    }

    [Fact]
    public void TheInnerComError_IsFound_AndUnknownErrorsKeepTheirMessage()
    {
        var wrapped = new InvalidOperationException("outer", new COMException("x", unchecked((int)0x80040154u)));
        Assert.Contains("not responding", AudioErrors.Describe(wrapped));
        Assert.Equal("plain", AudioErrors.Describe(new InvalidOperationException("plain")));
        Assert.Equal("slow", AudioErrors.Describe(new TimeoutException("slow")));
    }
}
