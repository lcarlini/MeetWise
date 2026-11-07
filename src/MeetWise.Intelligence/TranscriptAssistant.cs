using System.Globalization;
using System.Text;

namespace MeetWise.Intelligence;

public enum AssistantCommand { Summarize, KeyPoints, ActionItems, Chapters, TitleAndDescription, FollowUpEmail, CleanUp, Translate, Custom }

/// <summary>One line of a recording's transcript, whatever its source (live subtitles, import, YouTube).</summary>
public sealed record AssistantLine(double Start, string Speaker, string Text);

public sealed record AssistantRequest(AssistantCommand Command, string? Prompt = null, string? Language = null);

/// <summary>
/// The "AI Assistant" screen's brain: runs a command (summarize, chapters, translate, a free question...) over either the lines the user selected
/// or the whole recording. A recording that does not fit in the model's window is condensed in pieces first, so any length works.
/// </summary>
public sealed class TranscriptAssistant
{
    private readonly ILlmProvider _llm;

    public TranscriptAssistant(ILlmProvider llm) => _llm = llm;

    public static string Instruction(AssistantRequest r) => r.Command switch
    {
        AssistantCommand.Summarize => "Summarize this recording: a short paragraph, then the main points as bullets.",
        AssistantCommand.KeyPoints => "List the key points as concise bullets, most important first.",
        AssistantCommand.ActionItems => "List every action item, one per line as \"- [owner] task (due date)\". The owner is the PERSON NAMED as responsible in the sentence (for example \"Ana will update the manifests\" means the owner is Ana), not merely the speaker; if a speaker says \"I will\" use that speaker's label; if nobody is identified write \"unassigned\". Add the due date only when one was stated. Only include what was actually said.",
        AssistantCommand.Chapters => "Split the recording into chapters. For each chapter give the start timestamp (hh:mm:ss, taken from the transcript) and a short title, one per line.",
        AssistantCommand.TitleAndDescription => "Write a clear title (under 70 characters) and a two-sentence description for this recording.",
        AssistantCommand.FollowUpEmail => "Write a short, friendly follow-up email to the participants: thanks, a recap of decisions, the action items with owners, and next steps.",
        AssistantCommand.CleanUp => "Rewrite the transcript with correct punctuation, capitalisation and paragraphs, fixing obvious speech-recognition mistakes. Keep the meaning and the speaker names; do not summarize or add anything.",
        AssistantCommand.Translate => $"Translate the transcript into {LlmTranslator.LanguageName(r.Language ?? "en")}. Keep speaker names and timestamps. Output only the translation.",
        _ => r.Prompt?.Trim() is { Length: > 0 } p ? p : "Summarize this recording."
    };

    /// <summary>Commands whose answer is as long as the input: they run piece by piece and the pieces are joined, rather than condensed.</summary>
    public static bool IsRewrite(AssistantCommand c) => c is AssistantCommand.CleanUp or AssistantCommand.Translate;

    public static string FormatLines(IEnumerable<AssistantLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            sb.Append('[').Append(TimeSpan.FromSeconds(l.Start).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)).Append("] ");
            if (l.Speaker.Length > 0) sb.Append(l.Speaker).Append(": ");
            sb.AppendLine(l.Text);
        }
        return sb.ToString();
    }

    private static string Rules(bool selection, UserProfile? profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the AI assistant of a recording app. You answer questions and run commands about a transcript.");
        sb.AppendLine(selection ? "The user selected specific lines of the transcript; work only with those lines."
                                : "You are given the whole transcript of the recording.");
        sb.AppendLine("Use only what the transcript says; never invent facts, names, numbers or dates. If the transcript does not contain the answer, say so.");
        sb.AppendLine("Reply in the language of the transcript unless the user asks for another one. Be concise and well organised.");
        if (profile?.Occupation is { } occ) sb.AppendLine($"The user is {profile.Nickname ?? "a professional"}, {occ}.");
        return sb.ToString();
    }

    public async Task<string> RunAsync(IReadOnlyList<AssistantLine> lines, AssistantRequest request, bool isSelection = false,
        IReadOnlyList<ChatMessage>? history = null, UserProfile? profile = null, CancellationToken ct = default)
    {
        if (lines.Count == 0) return "There is no transcript to work with yet.";
        var instruction = Instruction(request);
        var transcript = FormatLines(lines);
        var budgetChars = Math.Max(2000, _llm.ContextTokens * 4 / 2); // half the window for the transcript, the rest for rules, history and the answer

        if (transcript.Length <= budgetChars)
            return (await Ask(Rules(isSelection, profile) + "\nTRANSCRIPT:\n" + transcript, history, instruction, ct)).Trim();

        var chunks = MeetingIntelligence.Chunk(transcript, _llm.ContextTokens);
        if (IsRewrite(request.Command))
        {
            var parts = new List<string>();
            foreach (var chunk in chunks)
                parts.Add((await Ask(Rules(isSelection, profile) + "\nTRANSCRIPT (part of a longer one):\n" + chunk, null, instruction, ct)).Trim());
            return string.Join("\n\n", parts);
        }

        // map: condense each piece for this task, keeping timestamps; reduce: answer from the condensed notes
        var notes = new List<string>();
        foreach (var chunk in chunks)
            notes.Add((await Ask(Rules(isSelection, profile) + "\nTRANSCRIPT (part of a longer one):\n" + chunk, null,
                $"Condense this part for the task \"{instruction}\". Keep every fact, name, number, decision and timestamp that task needs; at most 300 words.", ct)).Trim());
        var condensed = string.Join("\n\n", notes.Select((n, i) => $"NOTES {i + 1} of {notes.Count}:\n{n}"));
        return (await Ask(Rules(isSelection, profile) + "\nThe recording was long, so you are given condensed notes of its consecutive parts instead of the full transcript.\n\n" + condensed,
            history, instruction, ct)).Trim();
    }

    private Task<string> Ask(string system, IReadOnlyList<ChatMessage>? history, string instruction, CancellationToken ct)
    {
        var messages = new List<ChatMessage>();
        if (history is not null) messages.AddRange(history);
        messages.Add(new ChatMessage("user", instruction));
        return _llm.CompleteAsync(system, messages, 1500, ct);
    }
}
