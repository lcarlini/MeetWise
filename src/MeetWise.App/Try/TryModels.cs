namespace MeetWise.App.Try;

/// <summary>Audio = a clip is needed; Text = typed text; AudioOrText = either; AudioAndText = a clip plus optional typed text; None = nothing.</summary>
public enum TryInput { Audio, Text, AudioOrText, AudioAndText, None }

/// <summary>One line of a test result. <see cref="Samples"/> (when present) is the audio of that result.</summary>
public sealed record TryRow(string Label, string Details, float[]? Samples = null, int Rate = 16000);

public sealed class TryContext
{
    public required float[] Mic { get; init; }
    public float[]? Loopback { get; init; }
    public string Text { get; init; } = "";
    public string Language { get; init; } = "en";
    public required IProgress<string> Status { get; init; }
    public CancellationToken Ct { get; init; }
}

/// <summary>A test the benchmark and the "Test with my settings" boxes share. <paramref name="NameKey"/> and <paramref name="TipKey"/> are Loc keys.</summary>
public sealed record TryTest(string NameKey, string TipKey, TryInput Input, Func<TryContext, Task<IReadOnlyList<TryRow>>> Run,
    bool NeedsLanguage = false, string? TextHintKey = null, string? DefaultLanguage = null)
{
    public override string ToString() => Loc.T(NameKey);
}
