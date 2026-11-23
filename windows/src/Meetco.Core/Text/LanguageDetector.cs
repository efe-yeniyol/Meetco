using System.Globalization;

namespace Meetco.Core.Text;

/// <summary>
/// Lightweight on-device language identification, used when the transcription service does not report
/// a language. Non-Latin scripts are recognised by their letters (Japanese, Korean, Chinese, Arabic,
/// Persian, Urdu, Hebrew, Greek, Thai, Hindi, Tamil, Kannada, Armenian, Russian, Ukrainian…); Latin-script
/// languages by their most common words. Returns null when unsure: the language is only a hint in the
/// transcript, and the assistant model reads the text itself.
/// </summary>
public static class LanguageDetector
{
    private static readonly char[] WordSeparators =
        [' ', ',', '.', '?', '!', ';', ':', '"', '\'', '(', ')', '\n', '\t', '¿', '¡', '«', '»', '“', '”', '’'];

    /// <summary>Frequent, fairly distinctive words per Latin-script language.</summary>
    private static readonly Dictionary<string, HashSet<string>> CommonWords = new()
    {
        ["en"] =
        [
            "the", "and", "you", "is", "are", "to", "of", "for", "can", "what", "how", "we", "it", "that", "this",
            "in", "on", "with", "your", "our", "about", "me", "do", "more", "have", "be", "will", "would", "could",
            "a", "an", "i", "my", "they", "there", "was", "were", "not", "but", "so", "if", "at", "from", "tell",
        ],
        ["tr"] =
        [
            "ve", "bir", "bu", "şu", "için", "ile", "ama", "gibi", "çok", "daha", "var", "yok", "biraz", "hakkında",
            "mı", "mi", "mu", "mü", "misiniz", "musunuz", "mısınız", "değil", "evet", "hayır", "ben", "sen", "biz",
            "siz", "olarak", "şey", "nasıl", "neden", "ne", "da", "de", "ki", "kadar", "sonra", "önce", "şimdi",
            "tamam", "bunu", "şunu", "benim", "bizim", "sizin", "olan", "oldu", "olur", "yani", "ise",
        ],
        ["de"] =
        [
            "der", "die", "das", "und", "ist", "nicht", "ich", "sie", "wir", "mit", "für", "auf", "ein", "eine",
            "sind", "haben", "wie", "was", "können", "uns", "mir", "auch", "noch", "über", "wenn", "oder", "aber",
        ],
        ["fr"] =
        [
            "le", "la", "les", "et", "est", "pas", "je", "vous", "nous", "une", "des", "pour", "dans", "qui",
            "avec", "sur", "sont", "ce", "cette", "au", "aux", "mais", "ou", "comment", "pourquoi", "êtes",
        ],
        ["es"] =
        [
            "el", "los", "las", "y", "es", "una", "por", "para", "con", "del", "como", "qué", "está", "son",
            "usted", "ustedes", "nosotros", "pero", "muy", "cómo", "cuál", "puede", "tiene", "hay", "también",
        ],
        ["it"] =
        [
            "il", "lo", "gli", "è", "non", "che", "per", "della", "sono", "come", "questo", "questa", "anche",
            "ma", "molto", "voi", "noi", "può", "perché", "quale", "ci", "nel", "alla",
        ],
        ["pt"] =
        [
            "os", "não", "uma", "você", "vocês", "são", "está", "com", "do", "da", "dos", "das", "também", "muito",
            "nós", "como", "qual", "pode", "isso", "isto", "mas", "em", "ao",
        ],
        ["nl"] =
        [
            "het", "een", "en", "niet", "ik", "je", "jij", "wij", "we", "met", "voor", "op", "van", "zijn",
            "hebben", "wat", "hoe", "ook", "maar", "u", "jullie", "kunnen", "dat", "dit",
        ],
        ["pl"] =
        [
            "nie", "jest", "się", "na", "że", "jak", "co", "czy", "są", "jestem", "pan", "pani", "państwo",
            "dla", "ale", "tak", "już", "może", "bardzo", "mamy",
        ],
        ["sv"] =
        [
            "och", "är", "det", "att", "inte", "jag", "du", "vi", "för", "på", "som", "har", "med", "vad",
            "hur", "kan", "också", "men", "ni",
        ],
        ["id"] =
        [
            "dan", "yang", "di", "ini", "itu", "tidak", "saya", "anda", "kami", "dengan", "untuk", "apa", "ada",
            "bisa", "akan", "sudah", "kita", "bagaimana",
        ],
    };

    /// <summary>Letters that (among the languages above) only one language uses.</summary>
    private static readonly Dictionary<char, string> DistinctiveLetters = new()
    {
        ['ğ'] = "tr", ['ı'] = "tr", ['ş'] = "tr",
        ['ß'] = "de",
        ['ñ'] = "es",
        ['ã'] = "pt", ['õ'] = "pt",
        ['ł'] = "pl", ['ą'] = "pl", ['ę'] = "pl", ['ś'] = "pl", ['ź'] = "pl", ['ż'] = "pl", ['ć'] = "pl", ['ń'] = "pl",
        ['å'] = "sv",
    };

    public static string? Detect(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 3) return null;
        return DetectByScript(trimmed) ?? DetectByWords(trimmed);
    }

    private static string? DetectByScript(string text)
    {
        int kana = 0, hangul = 0, han = 0, arabic = 0, hebrew = 0, greek = 0, thai = 0, devanagari = 0,
            tamil = 0, kannada = 0, armenian = 0, cyrillic = 0, latin = 0;
        foreach (var c in text)
        {
            switch (c)
            {
                case >= '぀' and <= 'ヿ': kana++; break;
                case >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ': hangul++; break;
                case >= '一' and <= '鿿' or >= '㐀' and <= '䶿': han++; break;
                case >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ': arabic++; break;
                case >= '֐' and <= '׿': hebrew++; break;
                case >= 'Ͱ' and <= 'Ͽ' or >= 'ἀ' and <= '῿': greek++; break;
                case >= '฀' and <= '๿': thai++; break;
                case >= 'ऀ' and <= 'ॿ': devanagari++; break;
                case >= '஀' and <= '௿': tamil++; break;
                case >= 'ಀ' and <= '೿': kannada++; break;
                case >= '԰' and <= '֏': armenian++; break;
                case >= 'Ѐ' and <= 'ӿ': cyrillic++; break;
                default:
                    if (char.IsLetter(c) && c < 'ɐ') latin++;
                    break;
            }
        }

        var nonLatin = kana + hangul + han + arabic + hebrew + greek + thai + devanagari + tamil + kannada + armenian + cyrillic;
        if (nonLatin == 0 || nonLatin < latin) return null;
        if (kana > 0) return "ja";
        var (script, _) = new (string Script, int Count)[]
        {
            ("ko", hangul), ("zh", han), ("arabic", arabic), ("he", hebrew), ("el", greek), ("th", thai),
            ("hi", devanagari), ("ta", tamil), ("kn", kannada), ("hy", armenian), ("cyrillic", cyrillic),
        }.MaxBy(entry => entry.Count);
        return script switch
        {
            "arabic" => ArabicScriptLanguage(text),
            "cyrillic" => CyrillicLanguage(text),
            _ => script,
        };
    }

    private static string ArabicScriptLanguage(string text)
    {
        if (text.IndexOfAny(['ٹ', 'ڈ', 'ڑ', 'ں', 'ے', 'ھ']) >= 0) return "ur";
        if (text.IndexOfAny(['پ', 'چ', 'ژ', 'گ', 'ک', 'ی']) >= 0) return "fa";
        return "ar";
    }

    private static string CyrillicLanguage(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.IndexOfAny(['ә', 'ғ', 'қ', 'ң', 'ө', 'ұ', 'ү', 'һ']) >= 0) return "kk";
        if (lower.IndexOfAny(['ђ', 'ћ', 'џ']) >= 0) return "sr";
        if (lower.IndexOfAny(['ѓ', 'ќ', 'ѕ']) >= 0) return "mk";
        if (lower.IndexOfAny(['љ', 'њ', 'ј']) >= 0) return "sr";
        if (lower.Contains('ў')) return "be";
        if (lower.IndexOfAny(['ї', 'є', 'ґ']) >= 0 || (lower.Contains('і') && !lower.Contains('ы'))) return "uk";
        if (lower.Contains('ъ') && !lower.Contains('ы') && !lower.Contains('э')) return "bg";
        return "ru";
    }

    private static string? DetectByWords(string text)
    {
        var scores = new Dictionary<string, double>();
        foreach (var character in text)
        {
            if (DistinctiveLetters.TryGetValue(char.ToLower(character, CultureInfo.GetCultureInfo("tr-TR")), out var letterLanguage))
            {
                scores[letterLanguage] = scores.GetValueOrDefault(letterLanguage) + 1;
            }
        }
        // Turkish lower case keeps "ı" apart; other languages compare with a plain "i".
        var words = text.ToLower(CultureInfo.GetCultureInfo("tr-TR")).Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            foreach (var (language, list) in CommonWords)
            {
                var candidate = language == "tr" ? word : word.Replace('ı', 'i');
                if (list.Contains(candidate)) scores[language] = scores.GetValueOrDefault(language) + 2;
            }
        }

        if (scores.Count == 0) return null;
        var ranked = scores.OrderByDescending(pair => pair.Value).ToList();
        if (ranked.Count > 1 && ranked[0].Value == ranked[1].Value) return null;
        return ranked[0].Key;
    }

    /// <summary>Maps an English language name (as returned by whisper verbose_json) to an ISO code.</summary>
    public static string? CodeForLanguageName(string name)
    {
        var lower = name.Trim().ToLowerInvariant();
        if (lower.Length == 2) return lower;
        if (Models.SpokenLanguage.ForStoredValue(lower) is { } language) return language.Code;
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (string.Equals(culture.EnglishName, lower, StringComparison.OrdinalIgnoreCase) && culture.TwoLetterISOLanguageName.Length == 2)
            {
                return culture.TwoLetterISOLanguageName;
            }
        }
        return null;
    }
}
