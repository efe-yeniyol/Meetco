import Foundation
import NaturalLanguage

/// On-device language identification used when the transcription API does not report a language.
enum LanguageDetector {
    static func detect(_ text: String) -> String? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.count >= 3 else { return nil }
        let recognizer = NLLanguageRecognizer()
        // Meetco is primarily tuned for English and Turkish conversations.
        recognizer.languageHints = [
            .english: 0.4, .turkish: 0.35, .german: 0.05, .french: 0.05, .spanish: 0.05, .italian: 0.03, .dutch: 0.02,
        ]
        recognizer.processString(trimmed)
        guard let (language, probability) = recognizer.languageHypotheses(withMaximum: 1).first,
              probability >= 0.3 else { return nil }
        return language.rawValue
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
