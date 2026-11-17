import Foundation

/// One transcribed utterance from a single audio source.
struct TranscriptSegment: Identifiable, Codable, Sendable, Hashable {
    let id: UUID
    let source: AudioSourceKind
    var text: String
    /// BCP-47 language code such as "en" or "tr" (detected, may be nil).
    var languageCode: String?
    let startedAt: Date
    let endedAt: Date
    /// True when the local heuristic thinks the segment contains a question.
    var isQuestion: Bool

    init(
        id: UUID = UUID(),
        source: AudioSourceKind,
        text: String,
        languageCode: String?,
        startedAt: Date,
        endedAt: Date,
        isQuestion: Bool
    ) {
        self.id = id
        self.source = source
        self.text = text
        self.languageCode = languageCode
        self.startedAt = startedAt
        self.endedAt = endedAt
        self.isQuestion = isQuestion
    }

    var languageDisplayName: String? {
        guard let languageCode else { return nil }
        return Locale.current.localizedString(forLanguageCode: languageCode)
    }
}
