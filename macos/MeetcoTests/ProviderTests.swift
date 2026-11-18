import XCTest
@testable import Meetco

private final class RecordingChatClient: OpenAIClientProtocol, @unchecked Sendable {
    var responses: [Result<String, OpenAIError>]
    private(set) var requests: [ChatCompletionRequest] = []

    init(_ responses: [Result<String, OpenAIError>]) { self.responses = responses }

    func createChatCompletion(_ request: ChatCompletionRequest) async throws -> ChatCompletionResponse { fatalError("unused") }

    func streamChatCompletion(_ request: ChatCompletionRequest) -> AsyncThrowingStream<String, Error> {
        requests.append(request)
        let next = responses.removeFirst()
        return AsyncThrowingStream { continuation in
            switch next {
            case .success(let text): continuation.yield(text); continuation.finish()
            case .failure(let error): continuation.finish(throwing: error)
            }
        }
    }

    func createTranscription(audio: Data, filename: String, options: TranscriptionOptions, verbose: Bool) async throws -> TranscriptionResponse {
        TranscriptionResponse(text: "", language: nil)
    }

    func listModels(apiKeyOverride: String?) async throws -> [String] { [] }
}

private final class RecordingAnthropicClient: AnthropicClientProtocol, @unchecked Sendable {
    var responses: [Result<String, OpenAIError>]
    private(set) var requests: [AnthropicMessageRequest] = []

    init(_ responses: [Result<String, OpenAIError>]) { self.responses = responses }

    func streamMessage(_ request: AnthropicMessageRequest) -> AsyncThrowingStream<String, Error> {
        requests.append(request)
        let next = responses.removeFirst()
        return AsyncThrowingStream { continuation in
            switch next {
            case .success(let text):
                for word in text.split(separator: " ", omittingEmptySubsequences: false).enumerated() {
                    continuation.yield((word.offset == 0 ? "" : " ") + word.element)
                }
                continuation.finish()
            case .failure(let error): continuation.finish(throwing: error)
            }
        }
    }

    func listModels(apiKeyOverride: String?) async throws -> [String] { [] }
}

private struct MemoryKeyStore: APIKeyStoring {
    var key: String?
    func loadAPIKey() throws -> String? { key }
    func saveAPIKey(_ key: String) throws {}
    func deleteAPIKey() throws {}
}

final class ProviderTests: XCTestCase {
    private let screen = ScreenContext(jpegData: Data([0xFF, 0xD8, 0xFF]), capturedAt: Date(), pixelWidth: 1, pixelHeight: 1)

    private func request(kind: Suggestion.Kind = .answer, provider: AssistantProvider, model: String = "m", screen: Bool = false) -> AssistantRequest {
        AssistantRequest(
            kind: kind,
            focusQuestion: "When can we launch?",
            transcript: [TranscriptLine(speaker: .system, text: "When can we launch?", languageCode: "en", timestamp: Date())],
            meetingTopic: nil,
            preferences: AssistantPreferences(responseLanguage: .auto, explanationLanguage: .none, style: .concise, userContext: ""),
            model: model,
            screenContext: screen ? self.screen : nil,
            provider: provider
        )
    }

    // MARK: OpenAI-compatible request shaping

    func testDeepSeekRequestUsesJSONModeMaxTokensAndNoImage() throws {
        let chat = OpenAIMeetingAssistant.chatRequest(for: request(kind: .recap, provider: .deepSeek, screen: true))
        XCTAssertEqual(chat.responseFormat?.type, "json_object")
        XCTAssertEqual(chat.maxTokens, 900)
        XCTAssertNil(chat.maxCompletionTokens)
        XCTAssertNil(chat.reasoningEffort)
        XCTAssertFalse(chat.messages.contains(where: \.hasImage), "DeepSeek has no image input")
        // Without strict schemas the schema must be spelled out (and the word JSON present for JSON mode).
        let body = try XCTUnwrap(String(data: chat.encodedBody(), encoding: .utf8))
        XCTAssertTrue(body.contains("key_points"))
        XCTAssertTrue(body.contains("JSON"))
    }

    func testOpenAIRequestIsUnchanged() {
        let chat = OpenAIMeetingAssistant.chatRequest(for: request(kind: .recap, provider: .openAI, model: "gpt-6.1-terra", screen: true))
        XCTAssertEqual(chat.responseFormat?.type, "json_schema")
        XCTAssertEqual(chat.maxCompletionTokens, 4000)
        XCTAssertNil(chat.maxTokens)
        XCTAssertEqual(chat.reasoningEffort, "none")
        XCTAssertTrue(chat.messages.contains(where: \.hasImage))
    }

    func testLocalModelDegradesStructuredOutputStepByStep() async throws {
        let client = RecordingChatClient([
            .failure(.http(status: 400, message: "response_format json_schema is not supported", code: nil, retryAfter: nil)),
            .failure(.http(status: 400, message: "'response_format.type' must be 'json_schema' or 'text'", code: nil, retryAfter: nil)),
            .success(#"{"summary": "We talked.", "key_points": [], "action_items": [], "open_questions": [], "language": "English", "meeting_topic": "Sync"}"#),
        ])
        let output = try await OpenAIMeetingAssistant(client: client).generate(request(kind: .recap, provider: .ollama))
        XCTAssertEqual(output.text, "We talked.")
        XCTAssertEqual(client.requests.map { $0.responseFormat?.type }, ["json_schema", "json_object", nil])
    }

    func testLocalModelDropsScreenshotWhenImagesUnsupported() async throws {
        let client = RecordingChatClient([
            .failure(.http(status: 400, message: "this model does not support image input", code: nil, retryAfter: nil)),
            .success("Next month."),
        ])
        let output = try await OpenAIMeetingAssistant(client: client).generate(request(provider: .lmStudio, screen: true))
        XCTAssertEqual(output.text, "Next month.")
        XCTAssertTrue(client.requests[0].messages.contains(where: \.hasImage))
        XCTAssertFalse(client.requests[1].messages.contains(where: \.hasImage))
    }

    func testClientSendsNoAuthorizationWithoutKeyForLocalServers() async throws {
        let keys = APIKeyProvider(store: MemoryKeyStore(key: nil))
        let local = OpenAIClient(keyProvider: keys, configuration: { OpenAIConfiguration(baseURL: URL(string: "http://127.0.0.1:9")!, requestTimeout: 1) }, requiresAPIKey: false)
        do {
            _ = try await local.listModels(apiKeyOverride: nil)
        } catch OpenAIError.missingAPIKey {
            XCTFail("Local servers must not require a key")
        } catch {
            // Nothing listens on port 9: a network error is expected.
        }
        let hosted = OpenAIClient(keyProvider: keys, configuration: { OpenAIConfiguration(baseURL: URL(string: "http://127.0.0.1:9")!, requestTimeout: 1) })
        do {
            _ = try await hosted.listModels(apiKeyOverride: nil)
            XCTFail("Expected missingAPIKey")
        } catch OpenAIError.missingAPIKey {}
    }

    // MARK: Anthropic

    func testAnthropicRequestShape() throws {
        let message = AnthropicMeetingAssistant.messageRequest(for: request(kind: .recap, provider: .anthropic, model: "claude-haiku-5-5", screen: true))
        XCTAssertEqual(message.model, "claude-haiku-5-5")
        XCTAssertTrue(message.system.contains("Meetco"))
        XCTAssertTrue(message.system.contains("key_points"), "Schema must be described in the prompt")
        XCTAssertEqual(message.messages.count, 1)
        XCTAssertEqual(message.messages[0].role, "user")
        let image = try XCTUnwrap(message.messages[0].content.first { $0.type == "image" })
        XCTAssertEqual(image.source?.mediaType, "image/jpeg")
        XCTAssertEqual(image.source?.data, Data([0xFF, 0xD8, 0xFF]).base64EncodedString())

        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(message)) as? [String: Any])
        XCTAssertEqual(json["max_tokens"] as? Int, 1200)
        XCTAssertEqual(json["stream"] as? Bool, true)
        XCTAssertNotNil(json["system"] as? String)
    }

    func testAnthropicStreamsPlainAnswer() async throws {
        let client = RecordingAnthropicClient([.success("We can launch at the end of the month.")])
        let output = try await AnthropicMeetingAssistant(client: client).generate(request(provider: .anthropic))
        XCTAssertEqual(output.text, "We can launch at the end of the month.")
        XCTAssertEqual(client.requests.first?.temperature, 0.5)
    }

    func testAnthropicDropsTemperatureWhenRejected() async throws {
        let client = RecordingAnthropicClient([
            .failure(.http(status: 400, message: "temperature is not supported for this model", code: nil, retryAfter: nil)),
            .success("Soon."),
        ])
        let output = try await AnthropicMeetingAssistant(client: client).generate(request(provider: .anthropic))
        XCTAssertEqual(output.text, "Soon.")
        XCTAssertNil(client.requests[1].temperature)
    }

    // MARK: Routing

    func testRouterPicksClientAndNamesProviderInErrors() async throws {
        let deepSeek = RecordingChatClient([.success("Yes."), .failure(.http(status: 401, message: "bad key", code: nil, retryAfter: nil))])
        let anthropic = RecordingAnthropicClient([.success("Sure.")])
        let router = ProviderRoutingAssistant(chatClients: [.deepSeek: deepSeek], anthropic: anthropic)

        let fromDeepSeek = try await router.generate(request(provider: .deepSeek))
        XCTAssertEqual(fromDeepSeek.text, "Yes.")
        let fromClaude = try await router.generate(request(provider: .anthropic))
        XCTAssertEqual(fromClaude.text, "Sure.")

        do {
            _ = try await router.generate(request(provider: .deepSeek))
            XCTFail("Expected an error")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("DeepSeek rejected the API key"), error.localizedDescription)
            XCTAssertTrue(error.localizedDescription.contains("Settings → Assistant"))
        }
    }

    // MARK: Settings & keys

    @MainActor
    func testSettingsKeepModelAndURLPerProvider() throws {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "ProviderTests-\(UUID().uuidString)"))
        let settings = SettingsStore(defaults: defaults)
        XCTAssertEqual(settings.assistantProvider, .openAI)
        XCTAssertEqual(settings.assistantModel, settings.chatModel)

        settings.assistantProvider = .deepSeek
        XCTAssertEqual(settings.assistantModel, "deepseek-chat")
        settings.assistantModel = "deepseek-reasoner"
        settings.setBaseURL("http://gateway.local/v1", for: .ollama)

        let reloaded = SettingsStore(defaults: defaults)
        XCTAssertEqual(reloaded.assistantProvider, .deepSeek)
        XCTAssertEqual(reloaded.assistantModel, "deepseek-reasoner")
        XCTAssertNotEqual(reloaded.chatModel, "deepseek-reasoner", "OpenAI's model is untouched")
        XCTAssertEqual(OpenAIConfiguration.current(for: .ollama, defaults: defaults).baseURL.absoluteString, "http://gateway.local/v1")
        XCTAssertEqual(OpenAIConfiguration.current(for: .lmStudio, defaults: defaults).baseURL, AssistantProvider.lmStudio.defaultBaseURL)
    }

    func testKeyRegistryReadiness() {
        let registry = APIKeyRegistry(openAI: APIKeyProvider(store: MemoryKeyStore(key: "sk-openai"))) { provider in
            MemoryKeyStore(key: provider == .anthropic ? "sk-ant-123" : nil)
        }
        XCTAssertTrue(registry.isReady(.openAI))
        XCTAssertTrue(registry.isReady(.anthropic))
        XCTAssertFalse(registry.isReady(.deepSeek))
        XCTAssertTrue(registry.isReady(.ollama), "Local servers need no key")
        XCTAssertNotEqual(AssistantProvider.deepSeek.keychainService, AssistantProvider.openAI.keychainService)
        XCTAssertEqual(AssistantProvider.openAI.keychainService, "io.github.meetco.Meetco.openai", "Existing OpenAI keys must keep working")
    }
}
