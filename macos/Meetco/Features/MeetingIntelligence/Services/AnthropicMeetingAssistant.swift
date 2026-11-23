import Foundation

/// Generates meeting suggestions with Claude through Anthropic's Messages API.
///
/// Uses the same prompts and parsing as the OpenAI path. The Messages API has no
/// `response_format`, so structured kinds get their JSON schema described in the prompt.
struct AnthropicMeetingAssistant: MeetingAssisting {
    let client: any AnthropicClientProtocol

    func generate(
        _ request: AssistantRequest,
        onPartial: @escaping @MainActor @Sendable (AssistantPartial) -> Void
    ) async throws -> AssistantOutput {
        var message = Self.messageRequest(for: request)
        var adjustments = 0
        while true {
            do {
                return try await stream(message, kind: request.kind, onPartial: onPartial)
            } catch OpenAIError.http(let status, let message400, _, _) where status == 400 && adjustments < 2 {
                adjustments += 1
                let lower = message400.lowercased()
                if lower.contains("temperature") && message.temperature != nil {
                    message.temperature = nil
                } else if lower.contains("image") && request.screenContext != nil {
                    var withoutImage = request
                    withoutImage.screenContext = nil
                    message = Self.messageRequest(for: withoutImage, temperature: message.temperature)
                } else {
                    throw OpenAIError.http(status: status, message: message400, code: nil, retryAfter: nil)
                }
                Log.assistant.notice("Retrying Anthropic request with adjusted parameters")
            }
        }
    }

    static func messageRequest(for request: AssistantRequest, temperature: Double? = 0.5) -> AnthropicMessageRequest {
        AnthropicMessageRequest(
            model: request.model,
            chatMessages: PromptBuilder.messages(for: request, inlineSchema: true),
            maxTokens: request.kind == .memory ? 1500 : (request.kind == .recap ? 1200 : 800),
            temperature: temperature
        )
    }

    private func stream(
        _ message: AnthropicMessageRequest,
        kind: Suggestion.Kind,
        onPartial: @escaping @MainActor @Sendable (AssistantPartial) -> Void
    ) async throws -> AssistantOutput {
        let streamsAnswer = kind == .answer || kind == .whatToSay
        var content = ""
        var lastPartial = AssistantPartial()
        for try await delta in client.streamMessage(message) {
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
}

/// Sends each request to the provider it names, and labels errors with that provider's name.
struct ProviderRoutingAssistant: MeetingAssisting {
    /// OpenAI-compatible client for each provider (everything except Anthropic).
    let chatClients: [AssistantProvider: any OpenAIClientProtocol]
    let anthropic: any AnthropicClientProtocol

    func generate(
        _ request: AssistantRequest,
        onPartial: @escaping @MainActor @Sendable (AssistantPartial) -> Void
    ) async throws -> AssistantOutput {
        do {
            switch request.provider.wire {
            case .anthropic:
                return try await AnthropicMeetingAssistant(client: anthropic).generate(request, onPartial: onPartial)
            case .openAICompatible:
                guard let client = chatClients[request.provider] else { throw OpenAIError.missingAPIKey }
                return try await OpenAIMeetingAssistant(client: client).generate(request, onPartial: onPartial)
            }
        } catch let error as OpenAIError {
            throw ProviderError(provider: request.provider, underlying: error)
        }
    }
}

/// An API error attributed to the assistant provider that produced it.
struct ProviderError: LocalizedError, Equatable {
    let provider: AssistantProvider
    let underlying: OpenAIError

    var errorDescription: String? {
        if provider.isLocal, case .network = underlying {
            return "Can't reach \(provider.shortName) at \(OpenAIConfiguration.current(for: provider).baseURL.absoluteString). Make sure it is running and a model is loaded."
        }
        return underlying.description(service: provider.shortName, settingsTab: "Assistant")
    }
}
