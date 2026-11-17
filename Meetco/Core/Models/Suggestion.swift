import Foundation

/// A secondary line inside a suggestion (recap key point, suggested question, …).
struct SuggestionItem: Identifiable, Codable, Sendable, Hashable {
    var id = UUID()
    var text: String
    var detail: String?
}

/// A piece of AI output shown in the Answers tab.
struct Suggestion: Identifiable, Codable, Sendable, Hashable {
    enum Kind: String, Codable, Sendable, CaseIterable {
        case answer
        case whatToSay
        case recap
        case questionsToAsk

        var title: String {
            switch self {
            case .answer: "Suggested answer"
            case .whatToSay: "What to say"
            case .recap: "Recap so far"
            case .questionsToAsk: "Questions to ask"
            }
        }

        var systemImage: String {
            switch self {
            case .answer: "text.bubble"
            case .whatToSay: "quote.bubble"
            case .recap: "list.bullet.rectangle"
            case .questionsToAsk: "questionmark.bubble"
            }
        }
    }

    var id = UUID()
    var kind: Kind
    var createdAt = Date()
    /// The detected question this suggestion responds to.
    var question: String?
    /// Main text: suggested answer, or recap summary.
    var text: String
    /// Optional explanation in the user's explanation language (Turkish by default).
    var explanation: String?
    /// Language of `text` as reported by the model (e.g. "English").
    var languageName: String?
    var confidence: Double?
    var needsMoreInfo = false
    var directedAtUser = true
    var items: [SuggestionItem] = []
    var actionItems: [String] = []
    var openQuestions: [String] = []
    var usedScreenContext = false
    var triggerSegmentID: UUID?
    var isAutomatic = false

    /// Short title for lists in the sidebar.
    var previewTitle: String {
        if let question, !question.isEmpty { return question }
        if !text.isEmpty { return text }
        return items.first?.text ?? kind.title
    }

    /// Plain text representation suitable for the clipboard.
    var clipboardText: String {
        switch kind {
        case .answer, .whatToSay:
            return text
        case .recap:
            var parts = [text]
            if !items.isEmpty { parts.append(items.map { "• \($0.text)" }.joined(separator: "\n")) }
            if !actionItems.isEmpty { parts.append("Action items:\n" + actionItems.map { "• \($0)" }.joined(separator: "\n")) }
            if !openQuestions.isEmpty { parts.append("Open questions:\n" + openQuestions.map { "• \($0)" }.joined(separator: "\n")) }
            return parts.joined(separator: "\n\n")
        case .questionsToAsk:
            return items.map(\.text).joined(separator: "\n")
        }
    }
}
