import XCTest
@testable import Meetco

/// Fake OpenAI client that returns canned chat content and records requests.
private final class FakeOpenAIClient: OpenAIClientProtocol, @unchecked Sendable {
    var responses: [Result<String, OpenAIError>]
    private(set) var requests: [ChatCompletionRequest] = []

    init(responses: [Result<String, OpenAIError>]) { self.responses = responses }

    func createChatCompletion(_ request: ChatCompletionRequest) async throws -> ChatCompletionResponse {
        requests.append(request)
        let next = responses.removeFirst()
        let content = try next.get()
        let json = try JSONSerialization.data(withJSONObject: ["choices": [["message": ["content": content], "finish_reason": "stop"]]])
        return try JSONDecoder().decode(ChatCompletionResponse.self, from: json)
    }

    func streamChatCompletion(_ request: ChatCompletionRequest) -> AsyncThrowingStream<String, Error> {
        requests.append(request)
        let next = responses.removeFirst()
        return AsyncThrowingStream { continuation in
            switch next {
            case .failure(let error):
                continuation.finish(throwing: error)
            case .success(let content):
                // Deliver in small pieces, like server-sent events.
                var index = content.startIndex
                while index < content.endIndex {
                    let end = content.index(index, offsetBy: 7, limitedBy: content.endIndex) ?? content.endIndex
                    continuation.yield(String(content[index..<end]))
                    index = end
                }
                continuation.finish()
            }
        }
    }

    func createTranscription(audio: Data, filename: String, options: TranscriptionOptions, verbose: Bool) async throws -> TranscriptionResponse {
        TranscriptionResponse(text: "", language: nil)
    }

    func listModels(apiKeyOverride: String?) async throws -> [String] { [] }
}

@MainActor
private final class PartialCollector {
    var partials: [AssistantPartial] = []
}

final class PromptAndAssistantTests: XCTestCase {
    private func request(kind: Suggestion.Kind = .answer, model: String = "gpt-4.1-mini") -> AssistantRequest {
        AssistantRequest(
            kind: kind,
            focusQuestion: "Can your platform support 30 concurrent users?",
            transcript: [
                TranscriptLine(speaker: .system, text: "Can your platform support 30 concurrent users?", languageCode: "en", timestamp: Date()),
            ],
            meetingTopic: nil,
            preferences: AssistantPreferences(responseLanguage: .auto, explanationLanguage: .turkish, style: .concise, userContext: "I work at Acme."),
            model: model
        )
    }

    func testPromptContainsTranscriptAndRules() {
        let messages = PromptBuilder.messages(for: request())
        XCTAssertEqual(messages.count, 2)
        let system = PromptBuilder.systemPrompt(for: request())
        XCTAssertTrue(system.contains("Never invent facts"))
        XCTAssertTrue(system.contains("Turkish"))
        let user = PromptBuilder.userPrompt(for: request())
        XCTAssertTrue(user.contains("Participant [en]: Can your platform support 30 concurrent users?"))
        XCTAssertTrue(user.contains("I work at Acme."))
    }

    func testSchemaIsStrict() throws {
        let schema = PromptBuilder.schema(for: .answer)
        guard case .array(let required)? = schema["required"],
              case .object(let properties)? = schema["properties"] else {
            return XCTFail("Unexpected schema shape")
        }
        XCTAssertEqual(required.count, properties.count)
        XCTAssertEqual(schema["additionalProperties"], .bool(false))
        // The answer must come right after the question so it can stream first.
        XCTAssertEqual(properties.prefix(3).map(\.key), ["question_detected", "question", "suggested_answer"])
    }

    func testRequestBodyKeepsSchemaOrder() throws {
        let chat = ChatCompletionRequest(
            model: "m", messages: [.user("hi")],
            responseFormat: .jsonSchema(name: "x", schema: PromptBuilder.schema(for: .answer)),
            temperature: nil, maxCompletionTokens: nil, reasoningEffort: nil
        )
        let body = try XCTUnwrap(String(data: chat.encodedBody(), encoding: .utf8))
        let question = try XCTUnwrap(body.range(of: "\"question\":"))
        let answer = try XCTUnwrap(body.range(of: "\"suggested_answer\":"))
        let topic = try XCTUnwrap(body.range(of: "\"meeting_topic\":"))
        XCTAssertLessThan(question.lowerBound, answer.lowerBound)
        XCTAssertLessThan(answer.lowerBound, topic.lowerBound)
        XCTAssertNotNil(try JSONSerialization.jsonObject(with: chat.encodedBody()))
    }

    func testAssistantStreamsPartials() async throws {
        let json = #"{"question_detected": true, "question": "Why us?", "suggested_answer": "Because your mission matches my experience.", "explanation": "Çünkü...", "language": "English", "directed_at_user": true, "needs_more_info": false, "confidence": 0.8, "meeting_topic": "Interview"}"#
        let client = FakeOpenAIClient(responses: [.success(json)])
        let collector = PartialCollector()
        let output = try await OpenAIMeetingAssistant(client: client).generate(request()) { partial in
            collector.partials.append(partial)
        }
        XCTAssertEqual(output.text, "Because your mission matches my experience.")
        let answers = await MainActor.run { collector.partials.map(\.answer) }
        XCTAssertGreaterThan(answers.count, 3, "Answer should arrive progressively")
        XCTAssertTrue(answers.contains { !$0.isEmpty && $0 != output.text }, "Should see an incomplete answer while streaming")
        XCTAssertEqual(answers.last, output.text)
    }

    func testAssistantParsesResponse() async throws {
        let client = FakeOpenAIClient(responses: [.success(#"{"question_detected": true, "question": "Q?", "directed_at_user": true, "language": "English", "suggested_answer": "Yes.", "explanation": "Evet.", "confidence": 0.9, "needs_more_info": false, "meeting_topic": "Capacity"}"#)])
        let output = try await OpenAIMeetingAssistant(client: client).generate(request())
        XCTAssertEqual(output.text, "Yes.")
        XCTAssertEqual(client.requests.first?.temperature, 0.5)
        // Spoken answers are requested as plain text so the first token is already the answer.
        XCTAssertNil(client.requests.first?.responseFormat)
    }

    func testStructuredKindsKeepSchema() async throws {
        let client = FakeOpenAIClient(responses: [.success(#"{"summary": "We talked.", "key_points": [], "action_items": [], "open_questions": [], "language": "English", "meeting_topic": "Sync"}"#)])
        let output = try await OpenAIMeetingAssistant(client: client).generate(request(kind: .recap))
        XCTAssertEqual(output.text, "We talked.")
        XCTAssertEqual(client.requests.first?.responseFormat?.type, "json_schema")
    }

    func testPlainAnswerStreamsFromFirstToken() async throws {
        let client = FakeOpenAIClient(responses: [.success("Yes, we handle thirty concurrent users comfortably.\n===\nEvet, otuz eşzamanlı kullanıcıyı rahatça destekliyoruz.")])
        let collector = PartialCollector()
        let output = try await OpenAIMeetingAssistant(client: client).generate(request()) { partial in
            collector.partials.append(partial)
        }
        XCTAssertEqual(output.text, "Yes, we handle thirty concurrent users comfortably.")
        XCTAssertEqual(output.explanation, "Evet, otuz eşzamanlı kullanıcıyı rahatça destekliyoruz.")
        XCTAssertTrue(output.questionDetected)
        let partials = await MainActor.run { collector.partials }
        XCTAssertEqual(partials.first?.answer, "Yes, we", "The first delta should already show the answer")
        XCTAssertFalse(partials.contains { $0.answer.contains("=") }, "The delimiter must never leak into the answer")
        XCTAssertEqual(partials.last?.answer, output.text)
    }

    func testPlainAnswerNone() async throws {
        let client = FakeOpenAIClient(responses: [.success("NONE")])
        let collector = PartialCollector()
        let output = try await OpenAIMeetingAssistant(client: client).generate(request()) { partial in
            collector.partials.append(partial)
        }
        XCTAssertFalse(output.questionDetected)
        XCTAssertTrue(output.text.isEmpty)
        let partials = await MainActor.run { collector.partials }
        XCTAssertFalse(partials.contains { !$0.answer.isEmpty }, "NONE must never be shown as an answer")
    }

    func testPlainAnswerParsing() {
        XCTAssertEqual(PlainAnswer.partial(from: "No").answer, "", "Could still become NONE")
        XCTAssertEqual(PlainAnswer.partial(from: "No, not yet").answer, "No, not yet")
        XCTAssertEqual(PlainAnswer.partial(from: "None of them did.").questionDetected, true)
        XCTAssertEqual(PlainAnswer.partial(from: "none.").questionDetected, false)
        XCTAssertEqual(PlainAnswer.split("Sure.\n==").answer, "Sure.")
        XCTAssertEqual(PlainAnswer.split("\"Sure, I can.\"").answer, "Sure, I can.")
        XCTAssertNil(PlainAnswer.split("Sure.\n===\n").explanation)
    }

    func testSpokenPromptIsFirstPersonPlainText() {
        let system = PromptBuilder.systemPrompt(for: request())
        XCTAssertTrue(system.contains("first person"))
        XCTAssertTrue(system.contains("==="))
        XCTAssertFalse(system.contains("JSON object"))
        XCTAssertTrue(PromptBuilder.userPrompt(for: request()).contains("NONE"))
        XCTAssertTrue(PromptBuilder.systemPrompt(for: request(kind: .recap)).contains("JSON object"))
    }

    func testAssistantDropsUnsupportedParameterAndRetries() async throws {
        let client = FakeOpenAIClient(responses: [
            .failure(.http(status: 400, message: "Unsupported parameter: 'reasoning_effort'", code: nil, retryAfter: nil)),
            .success(#"{"question_detected": true, "suggested_answer": "Yes."}"#),
        ])
        let output = try await OpenAIMeetingAssistant(client: client).generate(request(model: "gpt-5-mini"))
        XCTAssertEqual(output.text, "Yes.")
        XCTAssertEqual(client.requests.count, 2)
        XCTAssertEqual(client.requests[0].reasoningEffort, "minimal")
        XCTAssertEqual(client.requests[1].reasoningEffort, "low")
        XCTAssertEqual(ModelCatalog.fastestReasoningEffort(for: "gpt-6.1-terra"), "none")
        XCTAssertEqual(ModelCatalog.fastestReasoningEffort(for: "gpt-6-astra"), "low")
        XCTAssertNil(ModelCatalog.fastestReasoningEffort(for: "gpt-4.1"))
        XCTAssertNil(client.requests[1].temperature)
    }

    func testRequestEncodingOmitsNilFields() throws {
        let chat = ChatCompletionRequest(model: "m", messages: [.user("hi")], responseFormat: nil, temperature: nil, maxCompletionTokens: 10, reasoningEffort: nil)
        let json = try XCTUnwrap(String(data: JSONEncoder().encode(chat), encoding: .utf8))
        XCTAssertFalse(json.contains("temperature"))
        XCTAssertFalse(json.contains("reasoning_effort"))
        XCTAssertTrue(json.contains("max_completion_tokens"))
    }
}
