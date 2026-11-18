using Meetco.Core.Models;

namespace Meetco.Core.Assistant;

/// <summary>A transcript line passed to the assistant (decoupled from UI models).</summary>
public sealed record TranscriptLine(AudioSourceKind Speaker, string Text, string? LanguageCode, DateTimeOffset Timestamp)
{
    public static TranscriptLine From(TranscriptSegment segment) =>
        new(segment.Source, segment.Text, segment.LanguageCode, segment.StartedAt);
}

public sealed record AssistantPreferences(
    ResponseLanguage ResponseLanguage,
    ExplanationLanguage ExplanationLanguage,
    ResponseStyle Style,
    string UserContext);

/// <summary>Screenshot attached as optional visual context.</summary>
public sealed record ScreenContext(byte[] JpegData, DateTimeOffset CapturedAt, int PixelWidth, int PixelHeight);

public sealed record AssistantRequest
{
    public required SuggestionKind Kind { get; init; }
    /// <summary>A specific question to answer (user picked it), or the question that triggered automation.</summary>
    public string? FocusQuestion { get; init; }
    /// <summary>True when the user explicitly chose <see cref="FocusQuestion"/>.</summary>
    public bool FocusIsExplicit { get; init; }
    public required IReadOnlyList<TranscriptLine> Transcript { get; init; }
    public string? MeetingTopic { get; init; }
    public required AssistantPreferences Preferences { get; init; }
    public required string Model { get; init; }
    public ScreenContext? ScreenContext { get; init; }
    /// <summary>Recent suggestions, so the model avoids repeating itself.</summary>
    public IReadOnlyList<string> PreviousAnswers { get; init; } = [];
}

/// <summary>An answer that is still being streamed.</summary>
public sealed record AssistantPartial(bool? QuestionDetected = null, string? Question = null, string Answer = "", string? Explanation = null)
{
    public static AssistantPartial Extract(string content)
    {
        if (!PlainAnswer.LooksLikeJson(content)) return PlainAnswer.Partial(content);
        return new AssistantPartial(
            PartialJson.Bool("question_detected", content),
            PartialJson.String("question", content)?.Value,
            PartialJson.String("suggested_answer", content)?.Value ?? "",
            PartialJson.String("explanation", content)?.Value);
    }
}

/// <summary>
/// Plain-text wire format for spoken answers.
///
/// Answers are not wrapped in JSON: the very first token the model writes is already part of
/// the answer, so it can be shown immediately. The format is:
///
///     &lt;words to say&gt;
///     ===
///     &lt;optional explanation&gt;
///
/// or the single word NONE when there is nothing to answer.
/// </summary>
public static class PlainAnswer
{
    public const string NoQuestionMarker = "NONE";
    public const string ExplanationDelimiter = "===";

    /// <summary>True when the model answered in the legacy JSON format (older prompts, stubborn models).</summary>
    public static bool LooksLikeJson(string content)
    {
        var start = content.TrimStart();
        return start.StartsWith('{') || start.StartsWith("```", StringComparison.Ordinal);
    }

    public static (string Answer, string? Explanation) Split(string content)
    {
        var index = content.IndexOf(ExplanationDelimiter, StringComparison.Ordinal);
        if (index >= 0)
        {
            var answer = Clean(content[..index]);
            var rest = content[(index + ExplanationDelimiter.Length)..].TrimStart('=');
            var explanation = Clean(rest);
            return (answer, explanation.Length == 0 ? null : explanation);
        }
        // The delimiter may be half-written while streaming ("=" or "==").
        return (Clean(content.Trim().TrimEnd('=')), null);
    }

    public static bool IsNoQuestion(string answer)
    {
        static bool Strip(char c) => char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c);
        var start = 0;
        var end = answer.Length;
        while (start < end && Strip(answer[start])) start++;
        while (end > start && Strip(answer[end - 1])) end--;
        return answer[start..end].ToUpperInvariant() == NoQuestionMarker;
    }

    /// <summary>State of an answer that is still being written.</summary>
    public static AssistantPartial Partial(string content)
    {
        var (answer, explanation) = Split(content);
        if (answer.Length == 0) return new AssistantPartial();
        if (IsNoQuestion(answer)) return new AssistantPartial(QuestionDetected: false);
        // "N", "NO", "NON" could still become NONE: wait for one more token before showing it.
        if (NoQuestionMarker.StartsWith(answer.ToUpperInvariant(), StringComparison.Ordinal)) return new AssistantPartial();
        return new AssistantPartial(QuestionDetected: true, Answer: answer, Explanation: explanation);
    }

    private static string Clean(string text)
    {
        var result = text.Trim();
        // Models sometimes wrap spoken text in quotes despite being told not to.
        if (result.Length >= 2 && ((result[0] == '"' && result[^1] == '"') || (result[0] == '“' && result[^1] == '”')))
        {
            result = result[1..^1];
        }
        return result;
    }
}

/// <summary>Parsed, validated model output.</summary>
public sealed record AssistantOutput
{
    public bool QuestionDetected { get; init; } = true;
    public string? Question { get; init; }
    public bool DirectedAtUser { get; init; } = true;
    public string? Language { get; init; }
    public string Text { get; init; } = "";
    public string? Explanation { get; init; }
    public double? Confidence { get; init; }
    public bool NeedsMoreInfo { get; init; }
    public string? MeetingTopic { get; init; }
    public IReadOnlyList<SuggestionItem> Items { get; init; } = [];
    public IReadOnlyList<string> ActionItems { get; init; } = [];
    public IReadOnlyList<string> OpenQuestions { get; init; } = [];
}

public enum AssistantErrorKind { InvalidResponse, EmptyResponse, Refused, Truncated }

public sealed class AssistantException(AssistantErrorKind kind, string detail = "") : Exception(detail)
{
    public AssistantErrorKind Kind { get; } = kind;
    public string Detail { get; } = detail;

    public override string Message => Kind switch
    {
        AssistantErrorKind.InvalidResponse => $"The AI response could not be understood ({Detail}). Try regenerating.",
        AssistantErrorKind.EmptyResponse => "The AI returned an empty response. Try regenerating.",
        AssistantErrorKind.Refused => $"The model declined to respond: {Detail}",
        _ => "The AI response was cut off. Try again or choose a faster model.",
    };
}

/// <summary>Produces suggestions (answers, recaps, questions) from meeting context.</summary>
public interface IMeetingAssistant
{
    /// <summary>Generates a suggestion, reporting partial answers while the model is still writing.</summary>
    Task<AssistantOutput> GenerateAsync(AssistantRequest request, Action<AssistantPartial> onPartial, CancellationToken cancellationToken);
}

/// <summary>Captures an image of the screen to give the assistant visual context.</summary>
public interface IScreenContextCapture
{
    Task<ScreenContext> CaptureMainDisplayAsync();
}
