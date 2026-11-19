import Foundation

/// A transcript line passed to the assistant (decoupled from UI models).
struct TranscriptLine: Sendable, Equatable {
    let speaker: AudioSourceKind
    let text: String
    let languageCode: String?
    let timestamp: Date

    init(speaker: AudioSourceKind, text: String, languageCode: String?, timestamp: Date) {
        self.speaker = speaker
        self.text = text
        self.languageCode = languageCode
        self.timestamp = timestamp
    }

    init(_ segment: TranscriptSegment) {
        self.init(speaker: segment.source, text: segment.text, languageCode: segment.languageCode, timestamp: segment.startedAt)
    }
}

struct AssistantPreferences: Sendable, Equatable {
    var responseLanguage: ResponseLanguage
    var explanationLanguage: ExplanationLanguage
    var style: ResponseStyle
    var userContext: String
}

/// Screenshot attached as optional visual context.
struct ScreenContext: Sendable, Equatable {
    let jpegData: Data
    let capturedAt: Date
    let pixelWidth: Int
    let pixelHeight: Int
}

struct AssistantRequest: Sendable {
    var kind: Suggestion.Kind
    /// A specific question to answer (user picked it), or the question that triggered automation.
    var focusQuestion: String?
    /// True when the user explicitly chose `focusQuestion`.
    var focusIsExplicit = false
    var transcript: [TranscriptLine]
    var meetingTopic: String?
    var preferences: AssistantPreferences
    var model: String
    var screenContext: ScreenContext?
    /// Recent suggestions, so the model avoids repeating itself.
    var previousAnswers: [String] = []
    /// Who answers: decides the wire protocol and which request options are sent.
    var provider: AssistantProvider = .openAI
}

/// An answer that is still being streamed.
struct AssistantPartial: Sendable, Equatable {
    var questionDetected: Bool?
    var question: String?
    var answer: String = ""
    var explanation: String?

    static func extract(from content: String) -> AssistantPartial {
        guard PlainAnswer.looksLikeJSON(content) else { return PlainAnswer.partial(from: content) }
        return AssistantPartial(
            questionDetected: PartialJSON.bool(forKey: "question_detected", in: content),
            question: PartialJSON.string(forKey: "question", in: content)?.value,
            answer: PartialJSON.string(forKey: "suggested_answer", in: content)?.value ?? "",
            explanation: PartialJSON.string(forKey: "explanation", in: content)?.value
        )
    }
}

/// Plain-text wire format for spoken answers.
///
/// Answers are not wrapped in JSON: the very first token the model writes is already part of
/// the answer, so it can be shown immediately. The format is:
///
///     <words to say>
///     ===
///     <optional explanation>
///
/// or the single word `NONE` when there is nothing to answer.
enum PlainAnswer {
    static let noQuestionMarker = "NONE"
    static let explanationDelimiter = "==="

    /// True when the model answered in the legacy JSON format (older prompts, stubborn models).
    static func looksLikeJSON(_ content: String) -> Bool {
        let start = content.drop(while: \.isWhitespace)
        return start.hasPrefix("{") || start.hasPrefix("```")
    }

    static func split(_ content: String) -> (answer: String, explanation: String?) {
        if let range = content.range(of: explanationDelimiter) {
            let answer = clean(String(content[..<range.lowerBound]))
            let rest = content[range.upperBound...].drop(while: { $0 == "=" })
            let explanation = clean(String(rest))
            return (answer, explanation.isEmpty ? nil : explanation)
        }
        // The delimiter may be half-written while streaming ("=" or "==").
        var answer = content.trimmingCharacters(in: .whitespacesAndNewlines)
        while answer.hasSuffix("=") { answer.removeLast() }
        return (clean(answer), nil)
    }

    static func isNoQuestion(_ answer: String) -> Bool {
        answer.trimmingCharacters(in: .whitespacesAndNewlines.union(.punctuationCharacters)).uppercased() == noQuestionMarker
    }

    /// State of an answer that is still being written.
    static func partial(from content: String) -> AssistantPartial {
        let (answer, explanation) = split(content)
        guard !answer.isEmpty else { return AssistantPartial() }
        if isNoQuestion(answer) { return AssistantPartial(questionDetected: false) }
        // "N", "NO", "NON" could still become NONE: wait for one more token before showing it.
        if noQuestionMarker.hasPrefix(answer.uppercased()) { return AssistantPartial() }
        return AssistantPartial(questionDetected: true, answer: answer, explanation: explanation)
    }

    private static func clean(_ text: String) -> String {
        var result = text.trimmingCharacters(in: .whitespacesAndNewlines)
        // Models sometimes wrap spoken text in quotes despite being told not to.
        if result.count >= 2, let first = result.first, let last = result.last,
           (first == "\"" && last == "\"") || (first == "“" && last == "”") {
            result = String(result.dropFirst().dropLast())
        }
        return result
    }
}

/// Parsed, validated model output.
struct AssistantOutput: Sendable, Equatable {
    var questionDetected = true
    var question: String?
    var directedAtUser = true
    var language: String?
    var text = ""
    var explanation: String?
    var confidence: Double?
    var needsMoreInfo = false
    var meetingTopic: String?
    var items: [SuggestionItem] = []
    var actionItems: [String] = []
    var openQuestions: [String] = []
}

enum AssistantError: LocalizedError, Equatable {
    case invalidResponse(String)
    case emptyResponse
    case refused(String)
    case truncated

    var errorDescription: String? {
        switch self {
        case .invalidResponse(let detail): "The AI response could not be understood (\(detail)). Try regenerating."
        case .emptyResponse: "The AI returned an empty response. Try regenerating."
        case .refused(let reason): "The model declined to respond: \(reason)"
        case .truncated: "The AI response was cut off. Try again or choose a faster model."
        }
    }
}
