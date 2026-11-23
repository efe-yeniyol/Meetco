import Foundation

/// Generates meeting suggestions with any OpenAI-compatible Chat Completions endpoint
/// (OpenAI, DeepSeek, Ollama, LM Studio, gateways). Requests follow `request.provider.capabilities`.
struct OpenAIMeetingAssistant: MeetingAssisting {
    let client: any OpenAIClientProtocol

    func generate(
        _ request: AssistantRequest,
        onPartial: @escaping @MainActor @Sendable (AssistantPartial) -> Void
    ) async throws -> AssistantOutput {
        var chat = Self.chatRequest(for: request)

        // Adapt if a model rejects an optional parameter (local servers vary by model).
        var adjustments = 0
        while true {
            do {
                return try await stream(chat, kind: request.kind, onPartial: onPartial)
            } catch OpenAIError.http(let status, let message, _, _) where (status == 400 || status == 422) && adjustments < 4 {
                adjustments += 1
                let lower = message.lowercased()
                if lower.contains("reasoning_effort") && chat.reasoningEffort != nil {
                    // The model does not know this effort level: fall back to "low", then to its default.
                    chat.reasoningEffort = chat.reasoningEffort == "low" ? nil : "low"
                } else if lower.contains("temperature") && chat.temperature != nil {
                    chat.temperature = nil
                } else if lower.contains("max_completion_tokens") && chat.maxCompletionTokens != nil {
                    chat.maxTokens = chat.maxCompletionTokens
                    chat.maxCompletionTokens = nil
                } else if lower.contains("max_tokens") && chat.maxTokens != nil {
                    chat.maxCompletionTokens = chat.maxTokens
                    chat.maxTokens = nil
                } else if (lower.contains("response_format") || lower.contains("json_schema") || lower.contains("json_object")) && chat.responseFormat != nil {
                    // Strict schema → JSON mode → schema described in the prompt only.
                    let hasImage = chat.messages.contains(where: \.hasImage)
                    chat.responseFormat = chat.responseFormat?.type == "json_schema" ? .jsonObject : nil
                    chat.messages = PromptBuilder.messages(for: Self.trimmed(request, keepImage: hasImage), inlineSchema: true)
                } else if lower.contains("image") && chat.messages.contains(where: \.hasImage) {
                    let inline = chat.responseFormat?.type != "json_schema"
                    chat.messages = PromptBuilder.messages(for: Self.trimmed(request, keepImage: false), inlineSchema: inline)
                } else {
                    throw OpenAIError.http(status: status, message: message, code: nil, retryAfter: nil)
                }
                Log.assistant.notice("Retrying chat request with adjusted parameters")
            }
        }
    }

    /// The first request, shaped by what the provider is known to accept.
    static func chatRequest(for request: AssistantRequest) -> ChatCompletionRequest {
        let caps = request.provider.capabilities
        let isReasoning = caps.reasoningEffort && ModelCatalog.isReasoningModel(request.model)
        // Spoken answers are plain text so the first token is already the answer (no schema
        // to compile, no JSON fields to write first). Recaps and question lists stay structured.
        let isSpoken = request.kind == .answer || request.kind == .whatToSay
        let responseFormat: ResponseFormat?
        switch (isSpoken, caps.structuredOutput) {
        case (true, _), (false, .none): responseFormat = nil
        case (false, .jsonObject): responseFormat = .jsonObject
        case (false, .jsonSchema):
            responseFormat = .jsonSchema(name: PromptBuilder.schemaName(for: request.kind), schema: PromptBuilder.schema(for: request.kind))
        }
        let tokenLimit = isReasoning ? 4000 : (request.kind == .memory ? 1500 : (request.kind == .recap ? 900 : 600))
        return ChatCompletionRequest(
            model: request.model,
            messages: PromptBuilder.messages(
                for: trimmed(request, keepImage: caps.images),
                inlineSchema: !isSpoken && caps.structuredOutput != .jsonSchema
            ),
            responseFormat: responseFormat,
            temperature: isReasoning ? nil : 0.5,
            maxCompletionTokens: caps.tokenLimit == .maxCompletionTokens ? tokenLimit : nil,
            reasoningEffort: isReasoning ? ModelCatalog.fastestReasoningEffort(for: request.model) : nil,
            maxTokens: caps.tokenLimit == .maxTokens ? tokenLimit : nil
        )
    }

    private static func trimmed(_ request: AssistantRequest, keepImage: Bool) -> AssistantRequest {
        guard !keepImage else { return request }
        var copy = request
        copy.screenContext = nil
        return copy
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
