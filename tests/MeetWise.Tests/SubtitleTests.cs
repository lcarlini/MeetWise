using MeetWise.Speech;

namespace MeetWise.Tests;

public class SubtitleTests
{
    [Theory]
    [InlineData(0, "00:00:00,000")]
    [InlineData(1.234, "00:00:01,234")]
    [InlineData(61.5, "00:01:01,500")]
    [InlineData(3723.0049, "01:02:03,005")]
    [InlineData(-5, "00:00:00,000")]
    public void SrtTime_MatchesTheLaseFormat(double seconds, string expected) => Assert.Equal(expected, SubtitleFormatter.SrtTime(seconds));

    [Fact]
    public void VttTime_UsesADot() => Assert.Equal("00:00:01.234", SubtitleFormatter.VttTime(1.234));

    [Fact]
    public void Wrap_BreaksAtWordsWithinTheLimit_AndKeepsLongWordsWhole()
    {
        var lines = SubtitleFormatter.Wrap("the quick brown fox jumps over the lazy dog near the quiet river bank today", 30);
        Assert.All(lines, l => Assert.True(l.Length <= 30, l));
        Assert.Equal("the quick brown fox jumps over the lazy dog near the quiet river bank today", string.Join(' ', lines));
        Assert.Equal(new[] { "short", "supercalifragilisticexpialidocious" }, SubtitleFormatter.Wrap("short supercalifragilisticexpialidocious", 20));
    }

    [Fact]
    public void Split_LeavesReadableCuesAlone()
    {
        var cue = new SubtitleCue(1, 3, "A short sentence.");
        Assert.Equal(new[] { cue }, SubtitleFormatter.Split(new[] { cue }));
    }

    [Fact]
    public void Split_CutsLongCues_AtWords_CoveringTheSameSpan()
    {
        var text = string.Join(' ', Enumerable.Repeat("meeting", 40)); // 40 words, 319 chars
        var pieces = SubtitleFormatter.Split(new[] { new SubtitleCue(10, 30, text) });
        Assert.True(pieces.Count >= 4);
        Assert.All(pieces, p => Assert.True(p.Text.Length <= SubtitleFormatter.MaxCharsPerLine * SubtitleFormatter.MaxLines, p.Text));
        Assert.All(pieces, p => Assert.True(p.End - p.Start <= SubtitleFormatter.MaxCueSeconds + 0.001));
        Assert.Equal(10, pieces[0].Start);
        Assert.Equal(30, pieces[^1].End, 6);
        for (int i = 1; i < pieces.Count; i++) Assert.Equal(pieces[i - 1].End, pieces[i].Start, 6); // no gaps, no overlap
        Assert.Equal(text, string.Join(' ', pieces.Select(p => p.Text))); // nothing lost, nothing cut inside a word
    }

    [Fact]
    public void Split_LongInTimeButShort_StillSplitsByTime()
    {
        var pieces = SubtitleFormatter.Split(new[] { new SubtitleCue(0, 21, "one two three four five six seven eight nine ten eleven twelve") });
        Assert.True(pieces.Count >= 3);
        Assert.All(pieces, p => Assert.True(p.End - p.Start <= 7.001));
    }

    private static readonly SubtitleCue[] Cues =
    {
        new(5, 8, "Second thing said.", "Others"),
        new(0.5, 3, "First thing said.", "Me"),
        new(9, 10, "  ", "Me") // blank text is dropped
    };

    [Fact]
    public void ToSrt_IsOrderedNumberedAndOffset()
    {
        var srt = SubtitleFormatter.ToSrt(Cues, offsetSeconds: 0.25);
        Assert.Equal("1\n00:00:00,750 --> 00:00:03,250\nFirst thing said.\n\n2\n00:00:05,250 --> 00:00:08,250\nSecond thing said.\n\n", srt);
    }

    [Fact]
    public void ToVtt_HasHeader_AndDotTimes()
    {
        var vtt = SubtitleFormatter.ToVtt(Cues);
        Assert.StartsWith("WEBVTT\n\n00:00:00.500 --> 00:00:03.000\nFirst thing said.", vtt);
    }

    [Fact]
    public void ToText_CanAddTimestampsAndSpeakers()
    {
        Assert.Equal("First thing said.\nSecond thing said.\n  \n", SubtitleFormatter.ToText(Cues));
        Assert.Equal("[00:00:00] Me: First thing said.\n[00:00:05] Others: Second thing said.\n[00:00:09] Me:   \n", SubtitleFormatter.ToText(Cues, timestamps: true, speakers: true));
    }

    [Fact]
    public void Srt_RoundTrips()
    {
        var original = new[] { new SubtitleCue(1.5, 4, "Hello there everyone."), new SubtitleCue(65, 70.25, "Second line of text.") };
        var parsed = SubtitleFormatter.Parse(SubtitleFormatter.ToSrt(original));
        Assert.Equal(original.Select(c => (c.Start, c.End, c.Text)), parsed.Select(c => (c.Start, c.End, c.Text)));
    }

    [Fact]
    public void Vtt_RoundTrips_AndShortTimesWithoutHoursAreAccepted()
    {
        var parsed = SubtitleFormatter.Parse("WEBVTT\n\n00:01.500 --> 00:04.000\nNo hours here.\n\n01:00:05.000 --> 01:00:07.000\nWith hours.\n");
        Assert.Equal((1.5, 4.0, "No hours here."), (parsed[0].Start, parsed[0].End, parsed[0].Text));
        Assert.Equal(3605, parsed[1].Start);
    }

    [Fact]
    public void Parse_RemovesStylingAndMultilineCuesBecomeOneText()
    {
        var parsed = SubtitleFormatter.Parse("1\n00:00:01,000 --> 00:00:03,000\n<i>Hello</i> &amp; <b>welcome</b>\nto the show\n");
        Assert.Equal("Hello & welcome to the show", Assert.Single(parsed).Text);
    }

    [Fact]
    public void Parse_YouTubeRollingAutoCaptions_BecomeOneLinePerCue_WithoutDuplicates()
    {
        // YouTube auto-captions: every cue repeats the previous line and adds a new one with inline word timestamps
        const string vtt = """
            WEBVTT
            Kind: captions
            Language: en

            00:00:00.000 --> 00:00:02.000 align:start position:0%
            hello<00:00:00.500><c> everyone</c><00:00:01.000><c> welcome</c>

            00:00:02.000 --> 00:00:02.010 align:start position:0%
            hello everyone welcome

            00:00:02.010 --> 00:00:04.000 align:start position:0%
            hello everyone welcome
            to<00:00:02.500><c> the</c><00:00:03.000><c> show</c>

            00:00:04.000 --> 00:00:04.010 align:start position:0%
            to the show

            00:00:04.010 --> 00:00:06.000 align:start position:0%
            to the show
            today<00:00:05.000><c> we</c><c> talk</c>
            """;
        var cues = SubtitleFormatter.Parse(vtt);
        Assert.Equal(new[] { "hello everyone welcome", "to the show", "today we talk" }, cues.Select(c => c.Text));
        Assert.True(cues.Zip(cues.Skip(1)).All(p => p.First.Start <= p.Second.Start));
    }

    [Fact]
    public void Parse_GarbageGivesNoCues() => Assert.Empty(SubtitleFormatter.Parse("this is not a subtitle file"));
}
