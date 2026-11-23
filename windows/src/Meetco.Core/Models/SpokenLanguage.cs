using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meetco.Core.Models;

/// <summary>
/// A language Meetco can listen to, answer in and translate into.
/// The list is the set of languages OpenAI's transcription models officially support; the assistant
/// models handle all of them too. The same list (same order, same names) is in the macOS app.
/// </summary>
/// <param name="Code">ISO 639-1 code, e.g. "de".</param>
/// <param name="EnglishName">English name, used in prompts ("Write the answer in German").</param>
/// <param name="NativeName">The language's own name, shown next to the English one in Settings.</param>
public sealed record SpokenLanguage(string Code, string EnglishName, string NativeName)
{
    /// <summary>"German (Deutsch)", or just "English" when both names are the same.</summary>
    public string Title => NativeName == EnglishName ? EnglishName : $"{EnglishName} ({NativeName})";

    /// <summary>Short badge, e.g. "DE".</summary>
    public string Badge => Code.ToUpperInvariant();

    /// <summary>Sorted by English name.</summary>
    public static readonly IReadOnlyList<SpokenLanguage> All =
    [
        new("af", "Afrikaans", "Afrikaans"),
        new("ar", "Arabic", "العربية"),
        new("hy", "Armenian", "Հայերեն"),
        new("az", "Azerbaijani", "Azərbaycanca"),
        new("be", "Belarusian", "Беларуская"),
        new("bs", "Bosnian", "Bosanski"),
        new("bg", "Bulgarian", "Български"),
        new("ca", "Catalan", "Català"),
        new("zh", "Chinese", "中文"),
        new("hr", "Croatian", "Hrvatski"),
        new("cs", "Czech", "Čeština"),
        new("da", "Danish", "Dansk"),
        new("nl", "Dutch", "Nederlands"),
        new("en", "English", "English"),
        new("et", "Estonian", "Eesti"),
        new("fi", "Finnish", "Suomi"),
        new("fr", "French", "Français"),
        new("gl", "Galician", "Galego"),
        new("de", "German", "Deutsch"),
        new("el", "Greek", "Ελληνικά"),
        new("he", "Hebrew", "עברית"),
        new("hi", "Hindi", "हिन्दी"),
        new("hu", "Hungarian", "Magyar"),
        new("is", "Icelandic", "Íslenska"),
        new("id", "Indonesian", "Bahasa Indonesia"),
        new("it", "Italian", "Italiano"),
        new("ja", "Japanese", "日本語"),
        new("kn", "Kannada", "ಕನ್ನಡ"),
        new("kk", "Kazakh", "Қазақ тілі"),
        new("ko", "Korean", "한국어"),
        new("lv", "Latvian", "Latviešu"),
        new("lt", "Lithuanian", "Lietuvių"),
        new("mk", "Macedonian", "Македонски"),
        new("ms", "Malay", "Bahasa Melayu"),
        new("mr", "Marathi", "मराठी"),
        new("mi", "Maori", "Māori"),
        new("ne", "Nepali", "नेपाली"),
        new("no", "Norwegian", "Norsk"),
        new("fa", "Persian", "فارسی"),
        new("pl", "Polish", "Polski"),
        new("pt", "Portuguese", "Português"),
        new("ro", "Romanian", "Română"),
        new("ru", "Russian", "Русский"),
        new("sr", "Serbian", "Српски"),
        new("sk", "Slovak", "Slovenčina"),
        new("sl", "Slovenian", "Slovenščina"),
        new("es", "Spanish", "Español"),
        new("sw", "Swahili", "Kiswahili"),
        new("sv", "Swedish", "Svenska"),
        new("tl", "Tagalog", "Tagalog"),
        new("ta", "Tamil", "தமிழ்"),
        new("th", "Thai", "ไทย"),
        new("tr", "Turkish", "Türkçe"),
        new("uk", "Ukrainian", "Українська"),
        new("ur", "Urdu", "اردو"),
        new("vi", "Vietnamese", "Tiếng Việt"),
        new("cy", "Welsh", "Cymraeg"),
    ];

    /// <summary>Other codes for the same language (Norwegian Bokmål/Nynorsk, old Hebrew/Indonesian codes, Filipino).</summary>
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["nb"] = "no", ["nn"] = "no", ["iw"] = "he", ["in"] = "id", ["fil"] = "tl",
    };

    private static readonly Dictionary<string, SpokenLanguage> ByCode = All.ToDictionary(l => l.Code);

    /// <summary>The language for an ISO code ("de", "zh-Hans", "pt_BR"), or null.</summary>
    public static SpokenLanguage? ForCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var lower = code.Trim().ToLowerInvariant();
        var separator = lower.IndexOfAny(['-', '_']);
        var baseCode = separator < 0 ? lower : lower[..separator];
        return ByCode.GetValueOrDefault(Aliases.GetValueOrDefault(baseCode) ?? baseCode);
    }

    /// <summary>Accepts an ISO code or an English language name ("Turkish"), as saved by older versions.</summary>
    public static SpokenLanguage? ForStoredValue(string value) =>
        ForCode(value) ?? All.FirstOrDefault(l => string.Equals(l.EnglishName, value.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Language of suggested answers: the speaker's language (automatic, <see cref="Code"/> null) or a fixed one.</summary>
[JsonConverter(typeof(LanguageChoiceJsonConverter<ResponseLanguage>))]
public readonly record struct ResponseLanguage(string? Code) : ILanguageChoice<ResponseLanguage>
{
    public static ResponseLanguage Auto => new(null);
    public static string NoneValue => "auto";
    public static string NoneTitle => "Match the speaker (automatic)";
    public static ResponseLanguage Create(string? code) => new(code);
}

/// <summary>Language of the translation shown under an answer, or off (<see cref="Code"/> null).</summary>
[JsonConverter(typeof(LanguageChoiceJsonConverter<ExplanationLanguage>))]
public readonly record struct ExplanationLanguage(string? Code) : ILanguageChoice<ExplanationLanguage>
{
    public static ExplanationLanguage None => new(null);
    public static ExplanationLanguage Turkish => new("tr");
    public static ExplanationLanguage English => new("en");
    public static string NoneValue => "none";
    public static string NoneTitle => "Off";
    public static ExplanationLanguage Create(string? code) => new(code);
}

/// <summary>The language spoken in the meeting: detected automatically (<see cref="Code"/> null) or fixed for accuracy.</summary>
[JsonConverter(typeof(LanguageChoiceJsonConverter<TranscriptionLanguage>))]
public readonly record struct TranscriptionLanguage(string? Code) : ILanguageChoice<TranscriptionLanguage>
{
    public static TranscriptionLanguage Auto => new(null);
    public static string NoneValue => "auto";
    public static string NoneTitle => "Detect automatically";
    public static TranscriptionLanguage Create(string? code) => new(code);
}

/// <summary>A language setting that is either automatic/off (null code) or one <see cref="SpokenLanguage"/>.</summary>
public interface ILanguageChoice<TSelf> where TSelf : struct, ILanguageChoice<TSelf>
{
    string? Code { get; }
    /// <summary>Stored value of the automatic/off choice.</summary>
    static abstract string NoneValue { get; }
    /// <summary>Title of the automatic/off choice.</summary>
    static abstract string NoneTitle { get; }
    static abstract TSelf Create(string? code);
}

public static class LanguageChoice
{
    public static SpokenLanguage? Language<T>(this T choice) where T : struct, ILanguageChoice<T> => SpokenLanguage.ForCode(choice.Code);

    /// <summary>English name of the language, used in prompts. Null for automatic/off.</summary>
    public static string? LanguageName<T>(this T choice) where T : struct, ILanguageChoice<T> => choice.Language()?.EnglishName;

    public static string Title<T>(this T choice) where T : struct, ILanguageChoice<T> => choice.Language()?.Title ?? T.NoneTitle;

    /// <summary>The automatic/off choice first, then every language.</summary>
    public static IReadOnlyList<T> AllChoices<T>() where T : struct, ILanguageChoice<T> =>
        [T.Create(null), .. SpokenLanguage.All.Select(l => T.Create(l.Code))];

    /// <summary>Parses a stored value: an ISO code, the automatic/off value, or an English name saved by older versions.</summary>
    public static T? Parse<T>(string? value) where T : struct, ILanguageChoice<T>
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, T.NoneValue, StringComparison.OrdinalIgnoreCase)) return T.Create(null);
        return SpokenLanguage.ForStoredValue(value) is { } language ? T.Create(language.Code) : null;
    }

    public static string StoredValue<T>(this T choice) where T : struct, ILanguageChoice<T> => choice.Code ?? T.NoneValue;
}

/// <summary>Stores a language choice as its ISO code; unknown values fall back to automatic/off.</summary>
public sealed class LanguageChoiceJsonConverter<T> : JsonConverter<T> where T : struct, ILanguageChoice<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        LanguageChoice.Parse<T>(reader.TokenType == JsonTokenType.String ? reader.GetString() : null) ?? T.Create(null);

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.StoredValue());
}
