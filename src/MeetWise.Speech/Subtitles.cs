using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MeetWise.Speech;

public sealed record SubtitleCue(double Start, double End, string Text, string Speaker = "", string? Language = null);

/// <summary>Writes and reads subtitle files (SRT, WebVTT, plain text) and shapes cues so they are comfortable to read.</summary>
public static partial class SubtitleFormatter
{
    public const int MaxCharsPerLine = 42;
    public const int MaxLines = 2;
    public const double MaxCueSeconds = 7.0;

    // ---- time ----

    public static string SrtTime(double seconds) => Time(seconds, ',');
    public static string VttTime(double seconds) => Time(seconds, '.');

    private static string Time(double seconds, char separator)
    {
        long ms = Math.Max(0, (long)Math.Round(seconds * 1000));
        long h = Math.DivRem(ms, 3_600_000, out ms);
        long m = Math.DivRem(ms, 60_000, out ms);
        long s = Math.DivRem(ms, 1000, out ms);
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{s:00}{separator}{ms:000}");
    }

    // ---- shaping ----

    /// <summary>Greedy word wrap. A word longer than a line is kept whole rather than cut.</summary>
    public static IReadOnlyList<string> Wrap(string text, int maxChars = MaxCharsPerLine)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > maxChars)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(word);
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return lines;
    }

    /// <summary>
    /// Splits cues that are too long to read (more than two lines of text, or longer than seven seconds) at word boundaries, giving each
    /// piece a share of the time in proportion to its length, so the pieces still cover exactly the original span.
    /// </summary>
    public static IReadOnlyList<SubtitleCue> Split(IEnumerable<SubtitleCue> cues, double maxSeconds = MaxCueSeconds, int maxChars = MaxCharsPerLine * MaxLines)
    {
        var result = new List<SubtitleCue>();
        foreach (var cue in cues)
        {
            var text = string.Join(' ', cue.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length == 0) continue;
            double duration = Math.Max(0, cue.End - cue.Start);
            int byTime = duration <= maxSeconds ? 1 : (int)Math.Ceiling(duration / maxSeconds);
            int byText = (int)Math.Ceiling(text.Length / (double)maxChars);
            int pieces = Math.Max(byTime, byText);
            if (pieces <= 1) { result.Add(cue with { Text = text }); continue; }

            var chunks = Balance(text, pieces);
            int total = chunks.Sum(c => c.Length);
            var shares = chunks.Select(c => duration * c.Length / total).ToList();
            // proportional shares follow the speech, but no piece may stay on screen longer than the maximum
            if (shares.Any(sh => sh > maxSeconds + 1e-9)) shares = chunks.Select(_ => duration / chunks.Count).ToList();
            double start = cue.Start;
            for (int i = 0; i < chunks.Count; i++)
            {
                double end = i == chunks.Count - 1 ? cue.End : start + shares[i];
                result.Add(cue with { Start = start, End = end, Text = chunks[i] });
                start = end;
            }
        }
        return result;
    }

    /// <summary>Divides text into at most <paramref name="pieces"/> chunks of similar length, never inside a word.</summary>
    private static List<string> Balance(string text, int pieces)
    {
        var words = text.Split(' ');
        var chunks = new List<string>();
        int target = (int)Math.Ceiling(text.Length / (double)pieces);
        var current = new StringBuilder();
        foreach (var w in words)
        {
            if (current.Length > 0 && current.Length + 1 + w.Length > target && chunks.Count < pieces - 1)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(w);
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    // ---- writing ----

    /// <summary>SRT numbered from 1, in time order. <paramref name="offsetSeconds"/> shifts every cue (for example the audio start delay).</summary>
    public static string ToSrt(IEnumerable<SubtitleCue> cues, double offsetSeconds = 0)
    {
        var sb = new StringBuilder();
        int n = 1;
        foreach (var c in Split(cues.OrderBy(c => c.Start)))
        {
            sb.Append(n++).Append('\n')
              .Append(SrtTime(c.Start + offsetSeconds)).Append(" --> ").Append(SrtTime(Math.Max(c.End, c.Start + 0.2) + offsetSeconds)).Append('\n')
              .Append(string.Join('\n', Wrap(c.Text))).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string ToVtt(IEnumerable<SubtitleCue> cues, double offsetSeconds = 0)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var c in Split(cues.OrderBy(c => c.Start)))
        {
            sb.Append(VttTime(c.Start + offsetSeconds)).Append(" --> ").Append(VttTime(Math.Max(c.End, c.Start + 0.2) + offsetSeconds)).Append('\n')
              .Append(string.Join('\n', Wrap(c.Text))).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string ToText(IEnumerable<SubtitleCue> cues, bool timestamps = false, bool speakers = false)
    {
        var sb = new StringBuilder();
        foreach (var c in cues.OrderBy(c => c.Start))
        {
            if (timestamps) sb.Append('[').Append(Time(c.Start, '.')[..8]).Append("] ");
            if (speakers && c.Speaker.Length > 0) sb.Append(c.Speaker).Append(": ");
            sb.Append(c.Text).Append('\n');
        }
        return sb.ToString();
    }

    // ---- reading ----

    [GeneratedRegex(@"(?<s>\d+:\d{2}:\d{2}[,.]\d{3}|\d{1,2}:\d{2}[,.]\d{3})\s*-->\s*(?<e>\d+:\d{2}:\d{2}[,.]\d{3}|\d{1,2}:\d{2}[,.]\d{3})")]
    private static partial Regex TimingRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    public static double ParseTime(string t)
    {
        var parts = t.Replace(',', '.').Split(':');
        double h = parts.Length == 3 ? double.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        double m = double.Parse(parts[^2], CultureInfo.InvariantCulture);
        double s = double.Parse(parts[^1], CultureInfo.InvariantCulture);
        return h * 3600 + m * 60 + s;
    }

    /// <summary>
    /// Reads SRT or WebVTT. Styling tags are removed, and YouTube's rolling auto-captions (each cue repeats the previous line and adds a new one)
    /// are reduced to one line per cue with consecutive repeats merged.
    /// </summary>
    public static IReadOnlyList<SubtitleCue> Parse(string content)
    {
        var raw = new List<(double Start, double End, List<string> Lines)>();
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = TimingRegex().Match(lines[i]);
            if (!m.Success) continue;
            var textLines = new List<string>();
            for (i++; i < lines.Length && lines[i].Length > 0; i++) // a line holding only a space (YouTube) does not end the cue
            {
                var clean = System.Net.WebUtility.HtmlDecode(TagRegex().Replace(lines[i], "")).Trim();
                if (clean.Length > 0) textLines.Add(clean);
            }
            raw.Add((ParseTime(m.Groups["s"].Value), ParseTime(m.Groups["e"].Value), textLines));
        }

        var cues = new List<SubtitleCue>();
        string previous = "";
        foreach (var (start, end, textLines) in raw)
        {
            if (textLines.Count == 0) continue;
            // YouTube rolling captions: the first line repeats the previous cue and only the following lines are new
            var text = textLines.Count > 1 && previous.Length > 0 && textLines[0] == previous
                ? string.Join(' ', textLines.Skip(1))
                : string.Join(' ', textLines);
            if (text == previous && cues.Count > 0)
            {
                cues[^1] = cues[^1] with { End = Math.Max(cues[^1].End, end) };
                continue;
            }
            cues.Add(new SubtitleCue(start, end, text));
            previous = text;
        }
        return cues;
    }
}
