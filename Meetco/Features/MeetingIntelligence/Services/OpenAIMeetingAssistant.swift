import Foundation

/// Generates structured meeting suggestions with OpenAI Chat Completions.
struct OpenAIMeetingAssistant: MeetingAssisting {
    let client: any OpenAIClientProtocol

    func generate(
        _ request: AssistantRequest,
        onPartial: @escaping @MainActor @Sendable (AssistantPartial) -> Void
    ) async throws -> AssistantOutput {
        let isReasoning = ModelCatalog.isReasoningModel(request.model)
        // Spoken answers are plain text so the first token is already the answer (no schema
        // to compile, no JSON fields to write first). Recaps and question lists stay structured.
        let isSpoken = request.kind == .answer || request.kind == .whatToSay
        var chat = ChatCompletionRequest(
            model: request.model,
            messages: PromptBuilder.messages(for: request),
            responseFormat: isSpoken ? nil : .jsonSchema(name: PromptBuilder.schemaName(for: request.kind), schema: PromptBuilder.schema(for: request.kind)),
            temperature: isReasoning ? nil : 0.5,
            maxCompletionTokens: isReasoning ? 4000 : (request.kind == .recap ? 900 : 600),
            reasoningEffort: ModelCatalog.fastestReasoningEffort(for: request.model)
        )

        // Adapt once or twice if a model rejects an optional parameter.
        var adjustments = 0
        while true {
            do {
                return try await stream(chat, kind: request.kind, onPartial: onPartial)
            } catch OpenAIError.http(let status, let message, _, _) where status == 400 && adjustments < 3 {
                adjustments += 1
                let lower = message.lowercased()
                if lower.contains("reasoning_effort") && chat.reasoningEffort != nil {
                    // The model does not know this effort level: fall back to "low", then to its default.
                    chat.reasoningEffort = chat.reasoningEffort == "low" ? nil : "low"
                } else if lower.contains("temperature") && chat.temperature != nil {
                    chat.temperature = nil
                } else if (lower.contains("response_format") || lower.contains("json_schema")) && chat.responseFormat?.type == "json_schema" {
                    chat.responseFormat = .jsonObject
                } else if lower.contains("image") && chat.messages.contains(where: \.hasImage) {
                    chat.messages = PromptBuilder.messages(for: { var r = request; r.screenContext = nil; return r }())
                } else {
                    throw OpenAIError.http(status: status, message: message, code: nil, retryAfter: nil)
                }
                Log.assistant.notice("Retrying chat request with adjusted parameters")
            }
        }
    }

    /// Streams the completion, emitting partial answers so the UI can show words as they arrive.
    private func stream(
        _ chat: ChatCompletionRequest,
        kind: Suggestion.Kind,
        onPartial: @escaping @MainActor @Sendable (AssistantPartial) -> Void
    ) async throws -> AssistantOutput {
        let streamsAnswer = kind == .answer || kind == .whatToSay
        var content = ""
        var lastPartial = AssistantPartial()
        for try await delta in client.streamChatCompletion(chat) {
            try Task.checkCancellation()
            content += delta
            guard streamsAnswer else { continue }
            let partial = AssistantPartial.extract(from: content)
            if partial != lastPartial {
                lastPartial = partial
                await onPartial(partial)
            }
        }
        guard !content.isEmpty else { throw AssistantError.emptyResponse }
        do {
            return try ResponseParser.parse(content, kind: kind)
        } catch {
            if PlainAnswer.looksLikeJSON(content) && !content.trimmingCharacters(in: .whitespacesAndNewlines).hasSuffix("}") {
                throw AssistantError.truncated
            }
            throw error
        }
    }

    static func output(from response: ChatCompletionResponse, kind: Suggestion.Kind) throws -> AssistantOutput {
        guard let choice = response.choices.first else { throw AssistantError.emptyResponse }
        if let refusal = choice.message.refusal, !refusal.isEmpty { throw AssistantError.refused(refusal) }
        guard let content = choice.message.content, !content.isEmpty else {
            throw choice.finishReason == "length" ? AssistantError.truncated : AssistantError.emptyResponse
        }
        do {
            return try ResponseParser.parse(content, kind: kind)
        } catch {
            if choice.finishReason == "length" { throw AssistantError.truncated }
            throw error
        }
    }
}
