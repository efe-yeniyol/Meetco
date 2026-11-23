import Foundation

/// A language Meetco can listen to, answer in and translate into.
///
/// The list is the set of languages OpenAI's transcription models officially support; the assistant
/// models handle all of them too. The same list (same order, same names) is in the Windows app.
struct SpokenLanguage: Hashable, Identifiable, Sendable {
    /// ISO 639-1 code, e.g. "de".
    let code: String
    /// English name, used in prompts ("Write the answer in German").
    let englishName: String
    /// The language's own name, shown next to the English one in Settings.
    let nativeName: String

    var id: String { code }

    /// "German (Deutsch)", or just "English" when both names are the same.
    var title: String { nativeName == englishName ? englishName : "\(englishName) (\(nativeName))" }

    /// Short badge, e.g. "DE".
    var badge: String { code.uppercased() }

    static func language(forCode code: String?) -> SpokenLanguage? {
        guard let code = code?.lowercased(), !code.isEmpty else { return nil }
        let base = String(code.prefix { $0 != "-" && $0 != "_" })
        return byCode[aliases[base] ?? base]
    }

    /// Accepts an ISO code or an English language name ("turkish", "German"), as saved by older versions.
    static func language(forStoredValue value: String) -> SpokenLanguage? {
        if let language = language(forCode: value) { return language }
        let lower = value.lowercased()
        return all.first { $0.englishName.lowercased() == lower }
    }

    /// Other codes for the same language (Norwegian Bokmål/Nynorsk, old Hebrew/Indonesian codes, Filipino).
    private static let aliases = ["nb": "no", "nn": "no", "iw": "he", "in": "id", "fil": "tl"]

    private static let byCode: [String: SpokenLanguage] = Dictionary(uniqueKeysWithValues: all.map { ($0.code, $0) })

    /// Sorted by English name.
    static let all: [SpokenLanguage] = [
        SpokenLanguage(code: "af", englishName: "Afrikaans", nativeName: "Afrikaans"),
        SpokenLanguage(code: "ar", englishName: "Arabic", nativeName: "العربية"),
        SpokenLanguage(code: "hy", englishName: "Armenian", nativeName: "Հայերեն"),
        SpokenLanguage(code: "az", englishName: "Azerbaijani", nativeName: "Azərbaycanca"),
        SpokenLanguage(code: "be", englishName: "Belarusian", nativeName: "Беларуская"),
        SpokenLanguage(code: "bs", englishName: "Bosnian", nativeName: "Bosanski"),
        SpokenLanguage(code: "bg", englishName: "Bulgarian", nativeName: "Български"),
        SpokenLanguage(code: "ca", englishName: "Catalan", nativeName: "Català"),
        SpokenLanguage(code: "zh", englishName: "Chinese", nativeName: "中文"),
        SpokenLanguage(code: "hr", englishName: "Croatian", nativeName: "Hrvatski"),
        SpokenLanguage(code: "cs", englishName: "Czech", nativeName: "Čeština"),
        SpokenLanguage(code: "da", englishName: "Danish", nativeName: "Dansk"),
        SpokenLanguage(code: "nl", englishName: "Dutch", nativeName: "Nederlands"),
        SpokenLanguage(code: "en", englishName: "English", nativeName: "English"),
        SpokenLanguage(code: "et", englishName: "Estonian", nativeName: "Eesti"),
        SpokenLanguage(code: "fi", englishName: "Finnish", nativeName: "Suomi"),
        SpokenLanguage(code: "fr", englishName: "French", nativeName: "Français"),
        SpokenLanguage(code: "gl", englishName: "Galician", nativeName: "Galego"),
        SpokenLanguage(code: "de", englishName: "German", nativeName: "Deutsch"),
        SpokenLanguage(code: "el", englishName: "Greek", nativeName: "Ελληνικά"),
        SpokenLanguage(code: "he", englishName: "Hebrew", nativeName: "עברית"),
        SpokenLanguage(code: "hi", englishName: "Hindi", nativeName: "हिन्दी"),
        SpokenLanguage(code: "hu", englishName: "Hungarian", nativeName: "Magyar"),
        SpokenLanguage(code: "is", englishName: "Icelandic", nativeName: "Íslenska"),
        SpokenLanguage(code: "id", englishName: "Indonesian", nativeName: "Bahasa Indonesia"),
        SpokenLanguage(code: "it", englishName: "Italian", nativeName: "Italiano"),
        SpokenLanguage(code: "ja", englishName: "Japanese", nativeName: "日本語"),
        SpokenLanguage(code: "kn", englishName: "Kannada", nativeName: "ಕನ್ನಡ"),
        SpokenLanguage(code: "kk", englishName: "Kazakh", nativeName: "Қазақ тілі"),
        SpokenLanguage(code: "ko", englishName: "Korean", nativeName: "한국어"),
        SpokenLanguage(code: "lv", englishName: "Latvian", nativeName: "Latviešu"),
        SpokenLanguage(code: "lt", englishName: "Lithuanian", nativeName: "Lietuvių"),
        SpokenLanguage(code: "mk", englishName: "Macedonian", nativeName: "Македонски"),
        SpokenLanguage(code: "ms", englishName: "Malay", nativeName: "Bahasa Melayu"),
        SpokenLanguage(code: "mr", englishName: "Marathi", nativeName: "मराठी"),
        SpokenLanguage(code: "mi", englishName: "Maori", nativeName: "Māori"),
        SpokenLanguage(code: "ne", englishName: "Nepali", nativeName: "नेपाली"),
        SpokenLanguage(code: "no", englishName: "Norwegian", nativeName: "Norsk"),
        SpokenLanguage(code: "fa", englishName: "Persian", nativeName: "فارسی"),
        SpokenLanguage(code: "pl", englishName: "Polish", nativeName: "Polski"),
        SpokenLanguage(code: "pt", englishName: "Portuguese", nativeName: "Português"),
        SpokenLanguage(code: "ro", englishName: "Romanian", nativeName: "Română"),
        SpokenLanguage(code: "ru", englishName: "Russian", nativeName: "Русский"),
        SpokenLanguage(code: "sr", englishName: "Serbian", nativeName: "Српски"),
        SpokenLanguage(code: "sk", englishName: "Slovak", nativeName: "Slovenčina"),
        SpokenLanguage(code: "sl", englishName: "Slovenian", nativeName: "Slovenščina"),
        SpokenLanguage(code: "es", englishName: "Spanish", nativeName: "Español"),
        SpokenLanguage(code: "sw", englishName: "Swahili", nativeName: "Kiswahili"),
        SpokenLanguage(code: "sv", englishName: "Swedish", nativeName: "Svenska"),
        SpokenLanguage(code: "tl", englishName: "Tagalog", nativeName: "Tagalog"),
        SpokenLanguage(code: "ta", englishName: "Tamil", nativeName: "தமிழ்"),
        SpokenLanguage(code: "th", englishName: "Thai", nativeName: "ไทย"),
        SpokenLanguage(code: "tr", englishName: "Turkish", nativeName: "Türkçe"),
        SpokenLanguage(code: "uk", englishName: "Ukrainian", nativeName: "Українська"),
        SpokenLanguage(code: "ur", englishName: "Urdu", nativeName: "اردو"),
        SpokenLanguage(code: "vi", englishName: "Vietnamese", nativeName: "Tiếng Việt"),
        SpokenLanguage(code: "cy", englishName: "Welsh", nativeName: "Cymraeg"),
    ]
}

/// A language setting that is either automatic/off (`code == nil`) or one `SpokenLanguage`.
/// Stored as the ISO code; older versions stored English names ("turkish"), which still load.
protocol LanguageChoice: RawRepresentable, Hashable, Identifiable, Codable, Sendable, CaseIterable where RawValue == String {
    /// Raw value of the automatic/off choice.
    static var noneRawValue: String { get }
    /// Title of the automatic/off choice.
    static var noneTitle: String { get }
    var code: String? { get }
    init(code: String?)
}

extension LanguageChoice {
    init?(rawValue: String) {
        if rawValue == Self.noneRawValue || rawValue.isEmpty {
            self.init(code: nil)
        } else if let language = SpokenLanguage.language(forStoredValue: rawValue) {
            self.init(code: language.code)
        } else {
            return nil
        }
    }

    init(_ language: SpokenLanguage) { self.init(code: language.code) }

    var rawValue: String { code ?? Self.noneRawValue }
    var id: String { rawValue }
    var language: SpokenLanguage? { SpokenLanguage.language(forCode: code) }
    /// English name of the language, used in prompts. Nil for automatic/off.
    var languageName: String? { language?.englishName }
    var title: String { language?.title ?? Self.noneTitle }

    static var allCases: [Self] { [Self(code: nil)] + SpokenLanguage.all.map { Self(code: $0.code) } }
}
