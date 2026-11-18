using System.Text.RegularExpressions;

namespace Meetco.Core.Text;

/// <summary>
/// Cleans transcription output and removes well-known speech-to-text hallucinations
/// that appear when a chunk contains music, noise or near-silence.
/// </summary>
public static partial class TranscriptTextFilter
{
    private static readonly HashSet<string> Hallucinations =
    [
        "thank you", "thanks for watching", "thank you for watching", "thank you so much for watching",
        "please subscribe", "subscribe", "like and subscribe", "bye", "you", "so", "okay", "uh", "um",
        "altyazi m k", "altyazi", "izlediginiz icin tesekkurler", "abone olmayi unutmayin",
        "tesekkurler", "tesekkur ederim", "music", "applause", "silence",
    ];

    [GeneratedRegex(@"[\[\(][^\]\)]{0,40}[\]\)]")]
    private static partial Regex BracketedAnnotation();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex RepeatedWhitespace();

    public static string Clean(string text)
    {
        var result = text.Trim();
        // Remove bracketed sound annotations like "[Music]" or "(applause)".
        result = BracketedAnnotation().Replace(result, "");
        result = RepeatedWhitespace().Replace(result, " ");
        return result.Trim();
    }

    public static bool IsLikelyHallucination(string text)
    {
        var normalized = TextSimilarity.Normalized(text);
        if (normalized.Length == 0) return true;
        if (Hallucinations.Contains(normalized)) return true;
        // Single repeated token like "you you you you".
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 4 && words.Distinct().Count() == 1;
    }
}
