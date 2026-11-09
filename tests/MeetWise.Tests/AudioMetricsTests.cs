using MeetWise.Speech;

namespace MeetWise.Tests;

public class AudioMetricsTests
{
    private static float[] Burst(int rate, double seconds, double noise, double speech)
    {
        // alternating 1 s: noise only, then a tone on top of the noise
        var rnd = new Random(1);
        var x = new float[(int)(rate * seconds)];
        for (int i = 0; i < x.Length; i++)
        {
            bool talking = (i / rate) % 2 == 1;
            x[i] = (float)(noise * (rnd.NextDouble() * 2 - 1) + (talking ? speech * Math.Sin(2 * Math.PI * 300 * i / rate) : 0));
        }
        return x;
    }

    [Fact]
    public void Compare_ReportsNoiseRemoved_WhenOnlyTheBackgroundIsLowered()
    {
        var orig = Burst(16000, 10, 0.05, 0.3);
        var processed = Burst(16000, 10, 0.005, 0.3); // background 20 dB lower, voice the same
        var c = AudioMetrics.Compare(orig, 16000, processed, 16000);
        Assert.InRange(c.NoiseRemovedDb, 15, 25);
        Assert.InRange(c.VoiceChangeDb, -2, 2);
        Assert.True(c.ProcessedSnrDb > c.OriginalSnrDb + 10);
    }

    [Fact]
    public void Compare_ReportsVoiceDamage_WhenTheVoiceIsCut()
    {
        var orig = Burst(16000, 10, 0.02, 0.3);
        var quiet = Burst(16000, 10, 0.02, 0.03);
        Assert.True(AudioMetrics.Compare(orig, 16000, quiet, 16000).VoiceChangeDb < -10);
    }

    [Fact]
    public void Compare_WorksAcrossSampleRates()
    {
        var a = Burst(16000, 6, 0.02, 0.3);
        var b = Burst(48000, 6, 0.02, 0.3);
        Assert.InRange(AudioMetrics.Compare(a, 16000, b, 48000).VoiceChangeDb, -2, 2);
    }

    [Fact]
    public void ShortClip_GivesNoNumbers()
    {
        var (n, s) = AudioMetrics.Profile(new float[100], 16000);
        Assert.Equal(-99, n);
        Assert.Equal(-99, s);
    }

    [Theory]
    [InlineData("the quick brown fox", "the quick brown fox", 0)]
    [InlineData("The quick, brown fox!", "the quick brown fox", 0)]
    [InlineData("the quick brown fox", "the quick fox", 0.25)]
    [InlineData("the quick brown fox", "a quick brown box", 0.5)]
    [InlineData("", "", 0)]
    [InlineData("", "anything", 1)]
    public void WordErrorRate_CountsSubstitutionsInsertionsAndDeletions(string reference, string heard, double expected)
        => Assert.Equal(expected, AudioMetrics.WordErrorRate(reference, heard), 6);

    [Fact]
    public void Percentile_UsesNearestRank()
    {
        var v = Enumerable.Range(1, 100).Select(i => (double)i).ToList();
        Assert.Equal(50, AudioMetrics.Percentile(v, 0.5));
        Assert.Equal(99, AudioMetrics.Percentile(v, 0.99));
        Assert.Equal(0, AudioMetrics.Percentile(new List<double>(), 0.5));
    }

    [Fact]
    public void Erle_IsLevelDropInDb()
    {
        var before = Enumerable.Repeat(0.1f, 1000).ToArray();
        var after = Enumerable.Repeat(0.01f, 1000).ToArray();
        Assert.Equal(20, AudioMetrics.ErleDb(before, after), 3);
    }
}
