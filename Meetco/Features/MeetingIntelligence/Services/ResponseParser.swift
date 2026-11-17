import Foundation

/// Parses (and tolerates minor deviations in) the model's JSON output.
enum ResponseParser {
    static func parse(_ content: String, kind: Suggestion.Kind) throws -> AssistantOutput {
        guard !content.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw AssistantError.emptyResponse }
        if (kind == .answer || kind == .whatToSay) && !PlainAnswer.looksLikeJSON(content) {
            return try parsePlainAnswer(content, kind: kind)
        }
        let json = try jsonObject(from: content)
        var output = AssistantOutput()
        output.meetingTopic = string(json["meeting_topic"])
        output.language = string(json["language"])
        output.explanation = string(json["explanation"]) ?? string(json["turkish_explanation"])

        switch kind {
        case .answer, .whatToSay:
            output.text = string(json["suggested_answer"]) ?? string(json["answer"]) ?? ""
            output.questionDetected = bool(json["question_detected"]) ?? !output.text.isEmpty
            output.question = string(json["question"])
            output.directedAtUser = bool(json["directed_at_user"]) ?? true
            output.confidence = double(json["confidence"]).map { min(max($0, 0), 1) }
            output.needsMoreInfo = bool(json["needs_more_info"]) ?? false
            if output.text.isEmpty && (kind == .whatToSay || output.questionDetected) {
                throw AssistantError.invalidResponse("missing suggested_answer")
            }
        case .recap:
            output.text = string(json["summary"]) ?? ""
            output.items = strings(json["key_points"]).map { SuggestionItem(text: $0) }
            output.actionItems = strings(json["action_items"])
            output.openQuestions = strings(json["open_questions"])
            if output.text.isEmpty && output.items.isEmpty {
                throw AssistantError.invalidResponse("missing summary")
            }
        case .questionsToAsk:
            let raw = json["questions"] as? [Any] ?? []
            output.items = raw.compactMap { entry in
                if let text = entry as? String, !text.isEmpty { return SuggestionItem(text: text) }
                if let object = entry as? [String: Any], let text = string(object["question"]) {
                    return SuggestionItem(text: text, detail: string(object["explanation"]))
                }
                return nil
            }
            if output.items.isEmpty { throw AssistantError.invalidResponse("no questions") }
        }
        return output
    }

    /// Parses the plain-text answer format (see `PlainAnswer`).
    static func parsePlainAnswer(_ content: String, kind: Suggestion.Kind) throws -> AssistantOutput {
        let (answer, explanation) = PlainAnswer.split(content)
        var output = AssistantOutput()
        if answer.isEmpty || PlainAnswer.isNoQuestion(answer) {
            if kind == .whatToSay { throw AssistantError.emptyResponse }
            output.questionDetected = false
            return output
        }
        output.text = answer
        output.explanation = explanation
        return output
    }

    /// Extracts a JSON object even if wrapped in code fences or surrounded by prose.
    static func jsonObject(from content: String) throws -> [String: Any] {
        let trimmed = content.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw AssistantError.emptyResponse }
        if let object = decode(trimmed) { return object }
        if let start = trimmed.firstIndex(of: "{"), let end = trimmed.lastIndex(of: "}"), start < end,
           let object = decode(String(trimmed[start...end])) {
            return object
        }
        throw AssistantError.invalidResponse("invalid JSON")
    }

    private static func decode(_ text: String) -> [String: Any]? {
        guard let data = text.data(using: .utf8) else { return nil }
        return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    }

    private static func string(_ value: Any?) -> String? {
        guard let text = value as? String else { return nil }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }

    private static func strings(_ value: Any?) -> [String] {
        (value as? [Any] ?? []).compactMap { string($0) }
    }

    private static func bool(_ value: Any?) -> Bool? {
        if let number = value as? NSNumber { return number.boolValue }
        if let text = value as? String { return ["true", "yes", "1"].contains(text.lowercased()) }
        return nil
    }

    private static func double(_ value: Any?) -> Double? {
        if let number = value as? NSNumber { return number.doubleValue }
        if let text = value as? String { return Double(text) }
        return nil
    }
}
