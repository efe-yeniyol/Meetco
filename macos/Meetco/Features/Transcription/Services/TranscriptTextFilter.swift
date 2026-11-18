import Foundation

/// Cleans transcription output and removes well-known speech-to-text hallucinations
/// that appear when a chunk contains music, noise or near-silence.
enum TranscriptTextFilter {
    private static let hallucinations: Set<String> = [
        "thank you", "thanks for watching", "thank you for watching", "thank you so much for watching",
        "please subscribe", "subscribe", "like and subscribe", "bye", "you", "so", "okay", "uh", "um",
        "altyazi m k", "altyazi", "izlediginiz icin tesekkurler", "abone olmayi unutmayin",
        "tesekkurler", "tesekkur ederim", "music", "applause", "silence",
    ]

    static func clean(_ text: String) -> String {
        var result = text.trimmingCharacters(in: .whitespacesAndNewlines)
        // Remove bracketed sound annotations like "[Music]" or "(applause)".
        result = result.replacingOccurrences(of: #"[\[\(][^\]\)]{0,40}[\]\)]"#, with: "", options: .regularExpression)
        result = result.replacingOccurrences(of: #"\s{2,}"#, with: " ", options: .regularExpression)
        return result.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func isLikelyHallucination(_ text: String) -> Bool {
        let normalized = TextSimilarity.normalized(text)
        if normalized.isEmpty { return true }
        if hallucinations.contains(normalized) { return true }
        // Single repeated token like "you you you you".
        let words = normalized.split(separator: " ")
        if words.count >= 4 && Set(words).count == 1 { return true }
        return false
    }
}
