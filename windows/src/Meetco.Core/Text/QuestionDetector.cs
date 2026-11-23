using System.Text.RegularExpressions;

namespace Meetco.Core.Text;

/// <summary>
/// Fast, local heuristic that decides whether a transcript segment contains a question.
/// Question marks are recognised in every script (Latin, Arabic, CJK, Armenian…); word lists cover
/// English and Turkish. Phrases this heuristic misses in any language are still answered when
/// "Detect questions in any language" is on: the assistant model then decides (see SuggestionEngine).
/// </summary>
public sealed partial class QuestionDetector
{
    /// <summary>
    /// Question marks across scripts: ASCII, full-width (CJK), Arabic/Persian/Urdu, Armenian, Ethiopic,
    /// the Spanish opening mark and the small/vertical forms. (The Greek question mark is a semicolon,
    /// so Greek questions are left to the model.)
    /// </summary>
    public static readonly char[] QuestionMarks = ['?', '？', '؟', '⸮', '՞', '፧', '¿', '﹖', '︖'];

    private static readonly char[] TrailingClosers = [' ', '\t', '\r', '\n', '"', '\'', '”', '’', '»', '」', '』', ')'];

    public static bool ContainsQuestionMark(string text) => text.IndexOfAny(QuestionMarks) >= 0;

    /// <summary>True when the text ends with a question mark (ignoring closing quotes and spaces).</summary>
    public static bool EndsWithQuestionMark(string text)
    {
        var trimmed = text.TrimEnd(TrailingClosers);
        return trimmed.Length > 0 && Array.IndexOf(QuestionMarks, trimmed[^1]) >= 0;
    }

    private static readonly string[] EnglishStarters =
    [
        "what", "what's", "whats", "how", "how's", "why", "when", "where", "who", "whom", "whose", "which",
        "can you", "could you", "would you", "will you", "can we", "could we", "would it", "will it",
        "do you", "does", "did you", "did we", "are you", "are we", "are there", "is it", "is there", "is that",
        "have you", "has", "should we", "should i", "shall we", "may i", "might", "any thoughts", "any idea",
        "tell me", "walk me through", "explain", "describe", "what about", "how about", "can i", "could i",
        "give me an example", "give us an example", "talk me through", "talk us through", "share",
        "i'd like to hear", "i would like to hear", "i'd love to hear", "i'd like to know", "i would like to know",
        "let's talk about", "imagine", "suppose", "introduce yourself",
    ];

    private static readonly string[] EnglishInnerPhrases =
    [
        "can you", "could you", "would you", "do you", "are you", "have you", "did you", "will you",
        "what do you think", "your thoughts", "let me know", "wondering if", "wondering whether",
        "curious about", "curious if", "tell me about", "tell us about", "walk me through", "walk us through",
        "give me an example", "how would you", "what would you", "why do you", "why did you",
    ];

    private static readonly HashSet<string> TurkishQuestionWords =
    [
        "ne", "neden", "niye", "nicin", "nasil", "nerede", "nereye", "nereden", "nereli", "hangi", "hangisi",
        "kac", "kim", "kime", "kimin", "kimi", "acaba", "mudur", "midir", "nedir", "nelerdir",
        "anlatin", "anlatir", "bahsedin", "bahseder", "aciklayin", "anlat", "bahset",
    ];

    /// <summary>Turkish yes/no particles: mı/mi/mu/mü with personal suffixes.</summary>
    [GeneratedRegex(@"(?:^|\s)m[ıiuü](?:s[ıiuü]n(?:[ıiuü]z)?|y[ıiuü]m|y[ıiuü]z|d[ıiuü]r|yd[ıiuü])?(?=$|[\s\.,!?])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TurkishParticle();

    [GeneratedRegex(@"(?<=[.!?…])\s+|\r?\n+")]
    private static partial Regex SentenceBoundary();

    public bool IsQuestion(string text) => Score(text) >= 0.5;

    public double Score(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length < 2) return EndsWithQuestionMark(trimmed) ? 0.6 : 0;
        if (ContainsQuestionMark(trimmed)) return 1;

        var lastSentences = LastSentences(trimmed, 2).ToLowerInvariant();

        // English: the sentence starts like a question even if punctuation is missing.
        var lastSentence = LastSentences(trimmed, 1).ToLowerInvariant();
        if (EnglishStarters.Any(starter => lastSentence.StartsWith(starter + " ", StringComparison.Ordinal))) return 0.8;
        if (EnglishInnerPhrases.Any(phrase => lastSentences.Contains(phrase, StringComparison.Ordinal))) return 0.6;

        // Turkish: question particles or interrogative words.
        if (TurkishParticle().IsMatch(lastSentences)) return 0.85;
        var words = TextSimilarity.Normalized(lastSentences).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(TurkishQuestionWords.Contains)) return 0.6;

        return 0;
    }

    private static string LastSentences(string text, int count)
    {
        var sentences = SentenceBoundary().Split(text)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        if (sentences.Count == 0) return text;
        return string.Join(' ', sentences.Skip(Math.Max(0, sentences.Count - count)));
    }
}
