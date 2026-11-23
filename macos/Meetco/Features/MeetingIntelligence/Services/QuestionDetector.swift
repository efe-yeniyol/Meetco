import Foundation

/// Fast, local heuristic that decides whether a transcript segment contains a question.
///
/// Question marks are recognised in every script (Latin, Arabic, CJK, Greek, Armenian…); word lists
/// cover English and Turkish. Phrases this heuristic misses in any language are still answered when
/// "Detect questions in any language" is on: the assistant model then decides (see `SuggestionEngine`).
struct QuestionDetector: Sendable {
    /// Question marks across scripts: ASCII, full-width (CJK), Arabic/Persian/Urdu, Armenian, Ethiopic,
    /// the Spanish opening mark and the small/vertical forms. (The Greek question mark is canonically
    /// a semicolon, so Greek questions are left to the model.)
    static let questionMarks: Set<Character> = ["?", "？", "؟", "⸮", "՞", "፧", "¿", "﹖", "︖"]

    static func containsQuestionMark(_ text: String) -> Bool {
        text.contains { questionMarks.contains($0) }
    }

    /// True when the text ends with a question mark (ignoring closing quotes and spaces).
    static func endsWithQuestionMark(_ text: String) -> Bool {
        guard let last = text.trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: "\"'”’»」』)"))).last else { return false }
        return questionMarks.contains(last)
    }

    private static let englishStarters: [String] = [
        "what", "what's", "whats", "how", "how's", "why", "when", "where", "who", "whom", "whose", "which",
        "can you", "could you", "would you", "will you", "can we", "could we", "would it", "will it",
        "do you", "does", "did you", "did we", "are you", "are we", "are there", "is it", "is there", "is that",
        "have you", "has", "should we", "should i", "shall we", "may i", "might", "any thoughts", "any idea",
        "tell me", "walk me through", "explain", "describe", "what about", "how about", "can i", "could i",
        "give me an example", "give us an example", "talk me through", "talk us through", "share",
        "i'd like to hear", "i would like to hear", "i'd love to hear", "i'd like to know", "i would like to know",
        "let's talk about", "imagine", "suppose", "introduce yourself",
    ]

    private static let englishInnerPhrases: [String] = [
        "can you", "could you", "would you", "do you", "are you", "have you", "did you", "will you",
        "what do you think", "your thoughts", "let me know", "wondering if", "wondering whether",
        "curious about", "curious if", "tell me about", "tell us about", "walk me through", "walk us through",
        "give me an example", "how would you", "what would you", "why do you", "why did you",
    ]

    private static let turkishQuestionWords: Set<String> = [
        "ne", "neden", "niye", "nicin", "nasil", "nerede", "nereye", "nereden", "nereli", "hangi", "hangisi",
        "kac", "kim", "kime", "kimin", "kimi", "acaba", "mudur", "midir", "nedir", "nelerdir",
        "anlatin", "anlatir", "bahsedin", "bahseder", "aciklayin", "anlat", "bahset",
    ]

    /// Turkish yes/no particles: mı/mi/mu/mü with personal suffixes.
    private static let turkishParticle = try! NSRegularExpression(
        pattern: #"(?:^|\s)m[ıiuü](?:s[ıiuü]n(?:[ıiuü]z)?|y[ıiuü]m|y[ıiuü]z|d[ıiuü]r|yd[ıiuü])?(?=$|[\s\.,!?])"#,
        options: [.caseInsensitive]
    )

    func isQuestion(_ text: String) -> Bool {
        score(text) >= 0.5
    }

    func score(_ text: String) -> Double {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.split(whereSeparator: \.isWhitespace).count >= 2 else { return Self.endsWithQuestionMark(trimmed) ? 0.6 : 0 }
        if Self.containsQuestionMark(trimmed) { return 1 }

        let lastSentences = Self.lastSentences(trimmed, count: 2).lowercased()

        // English: the sentence starts like a question even if punctuation is missing.
        let lastSentence = Self.lastSentences(trimmed, count: 1).lowercased()
        if Self.englishStarters.contains(where: { lastSentence.hasPrefix($0 + " ") }) { return 0.8 }
        if Self.englishInnerPhrases.contains(where: { lastSentences.contains($0) }) { return 0.6 }

        // Turkish: question particles or interrogative words.
        let range = NSRange(lastSentences.startIndex..., in: lastSentences)
        if Self.turkishParticle.firstMatch(in: lastSentences, range: range) != nil { return 0.85 }
        let folded = TextSimilarity.normalized(lastSentences)
        let words = Set(folded.split(separator: " ").map(String.init))
        if !words.isDisjoint(with: Self.turkishQuestionWords) { return 0.6 }

        return 0
    }

    private static func lastSentences(_ text: String, count: Int) -> String {
        var sentences: [String] = []
        text.enumerateSubstrings(in: text.startIndex..., options: [.bySentences, .localized]) { substring, _, _, _ in
            if let substring, !substring.trimmingCharacters(in: .whitespaces).isEmpty {
                sentences.append(substring.trimmingCharacters(in: .whitespacesAndNewlines))
            }
        }
        if sentences.isEmpty { return text }
        return sentences.suffix(count).joined(separator: " ")
    }
}
