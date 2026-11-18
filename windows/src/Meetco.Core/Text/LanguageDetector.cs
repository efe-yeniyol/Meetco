using System.Globalization;

namespace Meetco.Core.Text;

/// <summary>
/// Lightweight on-device language identification used when the transcription API does not report
/// a language. Meetco is tuned for English and Turkish conversations; other languages return null
/// (the transcript then simply shows no language badge).
/// </summary>
public static class LanguageDetector
{
    private const string TurkishLetters = "çğıöşüÇĞİÖŞÜ";

    private static readonly char[] WordSeparators = [' ', ',', '.', '?', '!', ';', ':', '"', '\'', '(', ')', '\n', '\t'];

    private static readonly HashSet<string> TurkishWords =
    [
        "ve", "bir", "bu", "şu", "için", "ile", "ama", "gibi", "çok", "daha", "var", "yok", "biraz", "hakkında",
        "mı", "mi", "mu", "mü", "misiniz", "musunuz", "mısınız", "değil", "evet", "hayır", "ben", "sen", "biz",
        "siz", "olarak", "şey", "nasıl", "neden", "ne", "da", "de", "ki", "kadar", "sonra", "önce", "şimdi",
        "tamam", "bunu", "şunu", "benim", "bizim", "sizin", "olan", "oldu", "olur", "yani", "ise",
    ];

    private static readonly HashSet<string> EnglishWords =
    [
        "the", "and", "you", "is", "are", "to", "of", "for", "can", "what", "how", "we", "it", "that", "this",
        "in", "on", "with", "your", "our", "about", "me", "do", "more", "have", "be", "will", "would", "could",
        "a", "an", "i", "my", "they", "there", "was", "were", "not", "but", "so", "if", "at", "from", "tell",
    ];

    public static string? Detect(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 3) return null;

        var turkish = 0.0;
        var english = 0.0;
        foreach (var character in trimmed)
        {
            if (TurkishLetters.Contains(character)) turkish += 1;
        }
        var words = trimmed.ToLower(CultureInfo.GetCultureInfo("tr-TR"))
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            if (TurkishWords.Contains(word)) turkish += 2;
            // English words are compared in invariant lower case ("I" → "i", not Turkish "ı").
            if (EnglishWords.Contains(word.Replace('ı', 'i'))) english += 2;
        }

        if (turkish == 0 && english == 0) return null;
        return turkish > english ? "tr" : english > turkish ? "en" : null;
    }

    /// <summary>Maps an English language name (as returned by whisper verbose_json) to an ISO code.</summary>
    public static string? CodeForLanguageName(string name)
    {
        var lower = name.Trim().ToLowerInvariant();
        if (lower.Length == 2) return lower;
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (string.Equals(culture.EnglishName, lower, StringComparison.OrdinalIgnoreCase) && culture.TwoLetterISOLanguageName.Length == 2)
            {
                return culture.TwoLetterISOLanguageName;
            }
        }
        return lower switch
        {
            "turkish" => "tr",
            "english" => "en",
            "german" => "de",
            "french" => "fr",
            "spanish" => "es",
            _ => null,
        };
    }
}
