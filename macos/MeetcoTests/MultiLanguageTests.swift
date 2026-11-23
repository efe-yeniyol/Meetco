import XCTest
@testable import Meetco

final class MultiLanguageTests: XCTestCase {
    private let detector = QuestionDetector()

    // MARK: Language catalog and settings

    func testCatalogHasUniqueCodesAndKnownLanguages() {
        let codes = SpokenLanguage.all.map(\.code)
        XCTAssertEqual(Set(codes).count, codes.count)
        XCTAssertGreaterThanOrEqual(codes.count, 50)
        XCTAssertEqual(SpokenLanguage.language(forCode: "de")?.englishName, "German")
        XCTAssertEqual(SpokenLanguage.language(forCode: "zh-Hans")?.code, "zh")
        XCTAssertEqual(SpokenLanguage.language(forCode: "nb")?.code, "no")
        XCTAssertEqual(SpokenLanguage.language(forCode: "pt-BR")?.title, "Portuguese (Português)")
        XCTAssertEqual(SpokenLanguage.language(forCode: "en")?.title, "English")
    }

    func testLanguageChoicesLoadOldAndNewValues() {
        // Older versions stored English names.
        XCTAssertEqual(ResponseLanguage(rawValue: "german"), ResponseLanguage(code: "de"))
        XCTAssertEqual(ResponseLanguage(rawValue: "auto"), .auto)
        XCTAssertEqual(ExplanationLanguage(rawValue: "turkish"), .turkish)
        XCTAssertEqual(ExplanationLanguage(rawValue: "none"), ExplanationLanguage.none)
        XCTAssertEqual(TranscriptionLanguage(rawValue: "english")?.isoCode, "en")
        // New values are ISO codes.
        XCTAssertEqual(TranscriptionLanguage(rawValue: "ja")?.isoCode, "ja")
        XCTAssertEqual(ExplanationLanguage(code: "ko").rawValue, "ko")
        XCTAssertEqual(ExplanationLanguage(code: "ko").badge, "KO")
        XCTAssertEqual(ResponseLanguage(code: "fr").languageName, "French")
        XCTAssertNil(ResponseLanguage(rawValue: "klingon"))
        XCTAssertEqual(ResponseLanguage.allCases.count, SpokenLanguage.all.count + 1)
        XCTAssertEqual(ResponseLanguage.allCases.first, .auto)
    }

    @MainActor
    func testSettingsPersistAnyLanguage() throws {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "MultiLanguageTests-\(UUID().uuidString)"))
        defaults.set("spanish", forKey: SettingsKeys.responseLanguage)
        let settings = SettingsStore(defaults: defaults)
        XCTAssertEqual(settings.responseLanguage, ResponseLanguage(code: "es"), "old saved names still load")
        XCTAssertTrue(settings.detectQuestionsInAnyLanguage, "on by default")

        settings.explanationLanguage = ExplanationLanguage(code: "ar")
        settings.transcriptionLanguage = TranscriptionLanguage(code: "hi")
        settings.detectQuestionsInAnyLanguage = false
        let reloaded = SettingsStore(defaults: defaults)
        XCTAssertEqual(reloaded.explanationLanguage.languageName, "Arabic")
        XCTAssertEqual(reloaded.transcriptionLanguage.isoCode, "hi")
        XCTAssertFalse(reloaded.detectQuestionsInAnyLanguage)
    }

    func testPromptNamesAnyLanguage() {
        let preferences = AssistantPreferences(
            responseLanguage: ResponseLanguage(code: "ja"), explanationLanguage: ExplanationLanguage(code: "de"),
            style: .concise, userContext: ""
        )
        let request = AssistantRequest(kind: .answer, transcript: [], preferences: preferences, model: "m")
        let system = PromptBuilder.systemPrompt(for: request)
        XCTAssertTrue(system.contains("Always write suggested answers in Japanese."))
        XCTAssertTrue(system.contains("German translation"))
    }

    // MARK: Detection

    func testQuestionMarksInOtherScripts() {
        XCTAssertTrue(detector.isQuestion("你们的价格是多少？"))
        XCTAssertTrue(detector.isQuestion("ما هو السعر؟"))
        XCTAssertTrue(detector.isQuestion("¿Cuánto cuesta la licencia anual?"))
        XCTAssertTrue(detector.isQuestion("Wie lange dauert die Integration?"))
        XCTAssertTrue(QuestionDetector.endsWithQuestionMark("料金はいくらですか？」"))
        XCTAssertFalse(detector.isQuestion("Wir planen den Start im nächsten Quartal."))
    }

    func testLanguageDetectionBeyondEnglishAndTurkish() {
        XCTAssertEqual(LanguageDetector.detect("Können Sie mir mehr über die Integration mit unserem System erzählen"), "de")
        XCTAssertEqual(LanguageDetector.detect("Pouvez-vous nous en dire plus sur le calendrier du projet"), "fr")
        XCTAssertEqual(LanguageDetector.detect("プロジェクトのスケジュールについて教えてください"), "ja")
        XCTAssertEqual(LanguageDetector.detect("你们的产品支持哪些语言"), "zh")
    }

    func testHallucinationListIsNormalizedAndMultilingual() {
        for phrase in TranscriptTextFilter.hallucinations {
            XCTAssertEqual(TextSimilarity.normalized(phrase), phrase, "\(phrase) can never match")
        }
        XCTAssertTrue(TranscriptTextFilter.isLikelyHallucination("Sous-titres réalisés par la communauté d'Amara.org"))
        XCTAssertTrue(TranscriptTextFilter.isLikelyHallucination("ご視聴ありがとうございました"))
        XCTAssertFalse(TranscriptTextFilter.isLikelyHallucination("Merci pour la présentation détaillée."))
    }

    // MARK: Any-language question detection (probes)

    @MainActor
    func testProbeAnswersAQuestionTheHeuristicMissed() async throws {
        let assistant = ScriptedAssistant { _ in "Ich habe fünf Jahre Erfahrung." }
        let (engine, transcript) = try makeEngine(assistant)
        let segment = participant("Erzählen Sie mir etwas über Ihre Erfahrung", transcript)

        engine.handleNewSegment(segment, captureMode: .systemAndMicrophone)
        XCTAssertFalse(engine.isGenerating, "an unconfirmed probe shows no spinner")
        XCTAssertNil(engine.draft, "an unconfirmed probe shows no draft")

        try await waitUntil { engine.suggestions.count == 1 }
        XCTAssertNil(assistant.requests.first?.focusQuestion, "a probe sends no question hint")
        XCTAssertEqual(engine.suggestions.first?.question, segment.text)
        XCTAssertEqual(engine.suggestions.first?.text, "Ich habe fünf Jahre Erfahrung.")
    }

    @MainActor
    func testProbeDeclinedByModelLeavesNothing() async throws {
        let assistant = ScriptedAssistant { _ in nil }
        let (engine, transcript) = try makeEngine(assistant)
        engine.handleNewSegment(participant("Wir planen den Start im nächsten Quartal", transcript), captureMode: .systemAndMicrophone)
        try await waitUntil { assistant.requests.count == 1 }
        try await Task.sleep(for: .milliseconds(50))
        XCTAssertTrue(engine.suggestions.isEmpty)
        XCTAssertNil(engine.draft)
        XCTAssertNil(engine.lastError)
    }

    @MainActor
    func testNoProbeWhenTurnedOff() async throws {
        let assistant = ScriptedAssistant { _ in "Ja." }
        let (engine, transcript) = try makeEngine(assistant, anyLanguage: false)
        engine.handleNewSegment(participant("Erzählen Sie mir etwas über Ihre Erfahrung", transcript), captureMode: .systemAndMicrophone)
        try await Task.sleep(for: .milliseconds(50))
        XCTAssertTrue(assistant.requests.isEmpty)
    }

    @MainActor
    func testProbeDoesNotInterruptAnAnswerBeingWritten() async throws {
        let assistant = ScriptedAssistant(delay: .milliseconds(300)) { _ in "Our pricing is per seat." }
        let (engine, transcript) = try makeEngine(assistant)
        engine.handleNewSegment(participant("How does your pricing work?", transcript, isQuestion: true), captureMode: .systemAndMicrophone)
        try await waitUntil { engine.draft?.text.isEmpty == false }

        engine.handleNewSegment(participant("Wir haben ungefähr zweihundert Nutzer im Team", transcript, after: 4), captureMode: .systemAndMicrophone)
        try await waitUntil { engine.suggestions.count == 1 }
        XCTAssertEqual(assistant.requests.count, 1, "the probe waited for the confirmed answer")
        XCTAssertEqual(engine.suggestions.first?.text, "Our pricing is per seat.")
    }

    // MARK: Helpers

    @MainActor
    private func makeEngine(_ assistant: ScriptedAssistant, anyLanguage: Bool = true) throws -> (SuggestionEngine, TranscriptStore) {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "MultiLanguageTests-\(UUID().uuidString)"))
        let settings = SettingsStore(defaults: defaults)
        settings.detectQuestionsInAnyLanguage = anyLanguage
        let keys = APIKeyRegistry(openAI: APIKeyProvider(store: TestKeyStore(key: "sk-test"))) { _ in TestKeyStore(key: nil) }
        let transcript = TranscriptStore()
        let engine = SuggestionEngine(assistant: assistant, screenCapture: NoScreenCapture(), transcript: transcript, settings: settings, keys: keys)
        return (engine, transcript)
    }

    @MainActor
    private func participant(_ text: String, _ transcript: TranscriptStore, isQuestion: Bool = false, after seconds: TimeInterval = 0) -> TranscriptSegment {
        let start = Date().addingTimeInterval(seconds)
        let segment = TranscriptSegment(source: .system, text: text, languageCode: nil, startedAt: start,
                                        endedAt: start.addingTimeInterval(1), isQuestion: isQuestion)
        transcript.insert(segment)
        return segment
    }

    @MainActor
    private func waitUntil(timeout: TimeInterval = 2, _ condition: @MainActor () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() {
            if Date() > deadline { XCTFail("Timed out"); return }
            try await Task.sleep(for: .milliseconds(10))
        }
    }
}

/// Answers every request with `answer(request)`, or "nothing to answer" when it returns nil.
private final class ScriptedAssistant: MeetingAssisting, @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [AssistantRequest] = []
    private let delay: Duration
    private let answer: @Sendable (AssistantRequest) -> String?

    init(delay: Duration = .zero, answer: @escaping @Sendable (AssistantRequest) -> String?) {
        self.delay = delay
        self.answer = answer
    }

    var requests: [AssistantRequest] { lock.withLock { recorded } }

    func generate(
        _ request: AssistantRequest,
        onPartial: @escaping @MainActor @Sendable (AssistantPartial) -> Void
    ) async throws -> AssistantOutput {
        lock.withLock { recorded.append(request) }
        guard let text = answer(request) else { return AssistantOutput(questionDetected: false) }
        await onPartial(AssistantPartial(questionDetected: true, answer: text))
        try await Task.sleep(for: delay)
        return AssistantOutput(questionDetected: true, text: text)
    }
}

private struct NoScreenCapture: ScreenContextCapturing {
    func captureMainDisplay() async throws -> ScreenContext { throw CancellationError() }
}

private struct TestKeyStore: APIKeyStoring {
    var key: String?
    func loadAPIKey() throws -> String? { key }
    func saveAPIKey(_ key: String) throws {}
    func deleteAPIKey() throws {}
}
