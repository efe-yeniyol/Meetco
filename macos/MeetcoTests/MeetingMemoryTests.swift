import XCTest
@testable import Meetco

final class MeetingMemoryTests: XCTestCase {
    private let sample = MeetingMemory(
        userName: "Efe",
        participants: [MeetingParticipant(name: "Ayşe", role: "Product manager", organization: "Acme", notes: "Cares about SSO")],
        facts: ["Ayşe: budget is €50k for Q3", "Efe: rollout takes 6 weeks"],
        summary: "Acme is evaluating the platform for 200 users."
    )

    // MARK: Parsing and prompts

    func testParsesMemoryAndDropsDuplicates() throws {
        let json = """
        {"user_name": "", "participants": [
            {"name": "Ayşe", "role": "Product manager", "organization": "Acme", "notes": ""},
            {"name": "ayşe", "role": "", "organization": "", "notes": ""},
            {"name": "Customer's CTO", "role": "CTO", "organization": "", "notes": "Asked about EU hosting"}
        ], "facts": ["Budget is €50k", ""], "summary": "Pricing call.", "meeting_topic": "Acme pricing"}
        """
        let output = try ResponseParser.parse(json, kind: .memory)
        let memory = try XCTUnwrap(output.memory)
        XCTAssertNil(memory.userName)
        XCTAssertEqual(memory.participants.map(\.name), ["Ayşe", "Customer's CTO"])
        XCTAssertEqual(memory.participants[0].detail, "Product manager, Acme")
        XCTAssertNil(memory.participants[1].organization)
        XCTAssertEqual(memory.facts, ["Budget is €50k"])
        XCTAssertEqual(output.meetingTopic, "Acme pricing")
    }

    func testAnswerPromptUsesPeopleAndEarlierFacts() {
        var request = request(kind: .answer)
        request.memory = sample
        let user = PromptBuilder.userPrompt(for: request)
        XCTAssertTrue(user.contains("## People in this meeting"))
        XCTAssertTrue(user.contains("- Efe (the user"))
        XCTAssertTrue(user.contains("- Ayşe — Product manager, Acme. Cares about SSO"))
        XCTAssertTrue(user.contains("- Ayşe: budget is €50k for Q3"))
        XCTAssertTrue(user.contains("Acme is evaluating"))
        let system = PromptBuilder.systemPrompt(for: request)
        XCTAssertTrue(system.contains("ground truth for this meeting"))
        XCTAssertTrue(system.contains("Never guess a name or role"))
    }

    func testAnswerPromptWithoutMemoryHasNoPeopleSection() {
        XCTAssertFalse(PromptBuilder.userPrompt(for: request(kind: .answer)).contains("## People in this meeting"))
    }

    func testMemoryRequestPrompt() throws {
        var request = request(kind: .memory)
        request.memory = sample
        XCTAssertEqual(PromptBuilder.systemPrompt(for: request), PromptBuilder.memorySystemPrompt)
        let user = PromptBuilder.userPrompt(for: request)
        XCTAssertTrue(user.contains("## Existing memory\n{"))
        XCTAssertTrue(user.contains("\"participants\""))
        XCTAssertTrue(user.contains("## New transcript lines"))
        XCTAssertFalse(user.contains("## People in this meeting"), "the memory request sees the memory as JSON only")

        let schema = PromptBuilder.schema(for: .memory)
        guard case .object(let fields) = schema, let required = fields.first(where: { $0.key == "required" })?.value,
              case .array(let keys) = required else { return XCTFail("schema") }
        XCTAssertEqual(keys.count, 5)
        XCTAssertEqual(PromptBuilder.schemaName(for: .memory), "meeting_memory")
    }

    // MARK: Engine

    @MainActor
    func testEngineUpdatesAfterEnoughConversationAndSendsOnlyNewLines() async throws {
        let assistant = MemoryAssistant()
        let (engine, transcript, _) = try makeEngine(assistant)

        add("Hi.", to: transcript)
        engine.segmentAdded()
        XCTAssertTrue(assistant.requests.isEmpty, "too little new transcript")

        add("I'm Ayşe, product manager at Acme. We have about two hundred users, a budget for the third quarter, and we need to decide on a tool before the end of the month.", to: transcript)
        engine.segmentAdded()
        XCTAssertTrue(engine.isUpdating)
        await engine.waitForUpdate()
        XCTAssertEqual(engine.memory.participants.first?.name, "Ayşe")
        XCTAssertEqual(assistant.requests.first?.kind, .memory)
        XCTAssertEqual(assistant.requests.first?.transcript.count, 2)

        add(String(repeating: "We also need single sign-on and EU hosting for compliance. ", count: 7), to: transcript)
        engine.segmentAdded()
        await engine.waitForUpdate()
        XCTAssertEqual(assistant.requests.count, 2)
        guard assistant.requests.count == 2 else { return }
        XCTAssertEqual(assistant.requests[1].transcript.count, 1, "only lines since the last update")
        XCTAssertEqual(assistant.requests[1].memory?.participants.first?.name, "Ayşe", "the existing memory is sent along")

        engine.reset()
        XCTAssertTrue(engine.memory.isEmpty)
        XCTAssertNil(engine.current)
    }

    @MainActor
    func testEngineOffSendsNothingAndFailuresBackOff() async throws {
        let assistant = MemoryAssistant()
        let (engine, transcript, settings) = try makeEngine(assistant)
        settings.meetingMemoryEnabled = false
        add(String(repeating: "Long conversation. ", count: 30), to: transcript)
        engine.segmentAdded()
        XCTAssertTrue(assistant.requests.isEmpty)

        settings.meetingMemoryEnabled = true
        assistant.fails = true
        engine.segmentAdded()
        await engine.waitForUpdate()
        engine.segmentAdded()
        XCTAssertEqual(assistant.requests.count, 1, "waits before retrying after a failure")
        engine.segmentAdded(now: Date().addingTimeInterval(MeetingMemoryEngine.retryDelay + 1))
        await engine.waitForUpdate()
        XCTAssertEqual(assistant.requests.count, 2)
    }

    @MainActor
    func testAnswersIncludeTheMemory() async throws {
        let assistant = MemoryAssistant()
        let (memoryEngine, transcript, settings) = try makeEngine(assistant)
        let keys = APIKeyRegistry(openAI: APIKeyProvider(store: MemoryTestKeyStore(key: "sk-test"))) { _ in MemoryTestKeyStore(key: nil) }
        let engine = SuggestionEngine(assistant: assistant, screenCapture: MemoryTestScreen(), transcript: transcript,
                                      settings: settings, keys: keys, memory: memoryEngine)

        add("Hello everyone, thanks for joining. I'm Ayşe, I lead the product team at Acme and we are evaluating tools.", to: transcript)
        let segment = add("We have two hundred users. How long would the rollout take for us?", to: transcript, isQuestion: true)
        engine.handleNewSegment(segment, captureMode: .systemAndMicrophone)
        await memoryEngine.waitForUpdate()
        XCTAssertEqual(engine.meetingTopic, "Acme rollout", "the memory update reports the topic")

        engine.whatShouldISay()
        try await waitUntil { assistant.requests.contains { $0.kind == .whatToSay } }
        let answer = try XCTUnwrap(assistant.requests.last { $0.kind == .whatToSay })
        XCTAssertEqual(answer.memory?.participants.first?.name, "Ayşe")
    }

    // MARK: Helpers

    private func request(kind: Suggestion.Kind) -> AssistantRequest {
        AssistantRequest(
            kind: kind,
            transcript: [TranscriptLine(speaker: .system, text: "How long does it take?", languageCode: "en", timestamp: Date())],
            preferences: AssistantPreferences(responseLanguage: .auto, explanationLanguage: .none, style: .concise, userContext: ""),
            model: "m"
        )
    }

    @MainActor
    private func makeEngine(_ assistant: MemoryAssistant) throws -> (MeetingMemoryEngine, TranscriptStore, SettingsStore) {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "MeetingMemoryTests-\(UUID().uuidString)"))
        let settings = SettingsStore(defaults: defaults)
        let transcript = TranscriptStore()
        return (MeetingMemoryEngine(assistant: assistant, transcript: transcript, settings: settings), transcript, settings)
    }

    @MainActor
    @discardableResult
    private func add(_ text: String, to transcript: TranscriptStore, isQuestion: Bool = false) -> TranscriptSegment {
        let segment = TranscriptSegment(source: .system, text: text, languageCode: "en", startedAt: Date(),
                                        endedAt: Date().addingTimeInterval(1), isQuestion: isQuestion)
        transcript.insert(segment)
        return segment
    }

    @MainActor
    private func waitUntil(_ condition: @MainActor () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(2)
        while !condition() {
            if Date() > deadline { return XCTFail("Timed out") }
            try await Task.sleep(for: .milliseconds(10))
        }
    }
}

/// Returns a fixed memory for `.memory` requests and a short answer otherwise.
private final class MemoryAssistant: MeetingAssisting, @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [AssistantRequest] = []
    private var shouldFail = false

    var requests: [AssistantRequest] { lock.withLock { recorded } }
    var fails: Bool {
        get { lock.withLock { shouldFail } }
        set { lock.withLock { shouldFail = newValue } }
    }

    func generate(
        _ request: AssistantRequest,
        onPartial: @escaping @MainActor @Sendable (AssistantPartial) -> Void
    ) async throws -> AssistantOutput {
        lock.withLock { recorded.append(request) }
        if fails { throw AssistantError.emptyResponse }
        if request.kind == .memory {
            var output = AssistantOutput()
            output.memory = MeetingMemory(participants: [MeetingParticipant(name: "Ayşe", role: nil, organization: "Acme", notes: nil)])
            output.meetingTopic = "Acme rollout"
            return output
        }
        return AssistantOutput(text: "About six weeks.")
    }
}

private struct MemoryTestScreen: ScreenContextCapturing {
    func captureMainDisplay() async throws -> ScreenContext { throw CancellationError() }
}

private struct MemoryTestKeyStore: APIKeyStoring {
    var key: String?
    func loadAPIKey() throws -> String? { key }
    func saveAPIKey(_ key: String) throws {}
    func deleteAPIKey() throws {}
}
