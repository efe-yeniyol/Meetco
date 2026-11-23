import Foundation
import NaturalLanguage

/// On-device language identification used when the transcription API does not report a language.
enum LanguageDetector {
    /// Languages Meetco supports; restricting the recognizer to them avoids rare look-alikes.
    private static let constraints: [NLLanguage] = SpokenLanguage.all.flatMap { language -> [NLLanguage] in
        switch language.code {
        case "zh": [.simplifiedChinese, .traditionalChinese]
        case "no": [.norwegian]
        default: [NLLanguage(rawValue: language.code)]
        }
    }

    static func detect(_ text: String) -> String? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.count >= 3 else { return nil }
        let recognizer = NLLanguageRecognizer()
        recognizer.languageConstraints = constraints
        recognizer.processString(trimmed)
        guard let (language, probability) = recognizer.languageHypotheses(withMaximum: 1).first,
              probability >= 0.3 else { return nil }
        // Chinese is reported as zh-Hans / zh-Hant and Norwegian as nb; the transcript uses ISO 639-1 codes.
        return SpokenLanguage.language(forCode: language.rawValue)?.code ?? language.rawValue
    }

    /// Maps an English language name (as returned by Whisper's verbose_json) to an ISO code.
    static func code(forLanguageName name: String) -> String? {
        let lower = name.lowercased().trimmingCharacters(in: .whitespaces)
        if lower.count == 2 { return lower }
        let english = Locale(identifier: "en")
        for code in Locale.LanguageCode.isoLanguageCodes {
            if english.localizedString(forLanguageCode: code.identifier)?.lowercased() == lower {
                return code.identifier
            }
        }
        return nil
    }
}
