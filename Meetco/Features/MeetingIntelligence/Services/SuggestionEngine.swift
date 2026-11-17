import Foundation
import Observation

/// Decides *when* to ask the model for help and manages the resulting suggestions.
///
/// Automatic flow: a Participant segment that looks like a question starts a short debounce
/// (so the full question is captured), duplicates are skipped, and any in-flight automatic
/// request is cancelled when a newer question arrives. Manual actions (What should I say?,
/// Recap, Questions to ask, Regenerate) are always honored.
@MainActor
@Observable
final class SuggestionEngine {
    /// Wait after a question-like segment, in case the participant keeps talking.
    static let debounceInterval: TimeInterval = 0.25
    /// A segment that ends with a question mark is answered right away; if the participant
    /// keeps talking, `continueAutomaticAnswer` restarts the answer with the full question.
    static let questionMarkDebounce: TimeInterval = 0
    static let maxDebounceWait: TimeInterval = 1.0
    /// A follow-up phrase within this window is treated as the rest of the question.
    static let continuationWindow: TimeInterval = 2.5
    static let duplicateThreshold = 0.72

    private struct AutomaticTrigger {
        let segmentID: UUID
        let questionText: String
        let questionEnd: Date
        let firedAt: Date
    }

    private(set) var suggestions: [Suggestion] = []
    var selectedSuggestionID: UUID?
    /// The answer currently being streamed (shown live, word by word).
    private(set) var draft: Suggestion?
    private(set) var activeKinds: [Suggestion.Kind] = []
    var lastError: String?
    private(set) var meetingTopic: String?
    var pendingScreenContext: ScreenContext?
    private(set) var isCapturingScreen = false

    @ObservationIgnored private let assistant: any MeetingAssisting
    @ObservationIgnored private let screenCapture: any ScreenContextCapturing
    @ObservationIgnored private let transcript: TranscriptStore
    @ObservationIgnored private let settings: SettingsStore
    @ObservationIgnored private let keyProvider: APIKeyProvider

    @ObservationIgnored private var automaticTask: Task<Void, Never>?
    @ObservationIgnored private var manualTasks: [Suggestion.Kind: Task<Void, Never>] = [:]
    @ObservationIgnored private var debounceTask: Task<Void, Never>?
    @ObservationIgnored private var pendingQuestion: TranscriptSegment?
    @ObservationIgnored private var pendingSince: Date?
    @ObservationIgnored private var recentAutomaticQuestions: [String] = []
    @ObservationIgnored private var lastAutomatic: AutomaticTrigger?
    @ObservationIgnored private var draftRequestID: UUID?

    init(
        assistant: any MeetingAssisting,
        screenCapture: any ScreenContextCapturing,
        transcript: TranscriptStore,
        settings: SettingsStore,
        keyProvider: APIKeyProvider
    ) {
        self.assistant = assistant
        self.screenCapture = screenCapture
        self.transcript = transcript
        self.settings = settings
        self.keyProvider = keyProvider
    }

    // MARK: Derived state

    var isGenerating: Bool { !activeKinds.isEmpty }

    /// What the Answers view should show: the live draft, the selected suggestion, or the latest one.
    var currentSuggestion: Suggestion? {
        if let draft, selectedSuggestionID == nil { return draft }
        return displayedSuggestion
    }

    var isStreamingCurrent: Bool { draft != nil && currentSuggestion?.id == draft?.id }

    /// 1-based position of the displayed suggestion in history (newest = 1).
    var historyPosition: (index: Int, count: Int)? {
        guard let current = displayedSuggestion, let index = suggestions.firstIndex(where: { $0.id == current.id }) else { return nil }
        return (index + 1, suggestions.count)
    }

    func showOlder() {
        guard let position = historyPosition, position.index < position.count else { return }
        selectedSuggestionID = suggestions[position.index].id
    }

    func showNewer() {
        guard let position = historyPosition, position.index > 1 else { return }
        let newer = suggestions[position.index - 2]
        selectedSuggestionID = position.index - 2 == 0 ? nil : newer.id
    }

    var displayedSuggestion: Suggestion? {
        if let selectedSuggestionID, let selected = suggestions.first(where: { $0.id == selectedSuggestionID }) {
            return selected
        }
        return suggestions.first
    }

    var latestQuestionsToAsk: Suggestion? {
        suggestions.first { $0.kind == .questionsToAsk }
    }

    func isGenerating(_ kind: Suggestion.Kind) -> Bool { activeKinds.contains(kind) }

    // MARK: Automatic triggering

    func handleNewSegment(_ segment: TranscriptSegment, captureMode: AudioCaptureMode) {
        guard settings.autoSuggest, keyProvider.hasKey else { return }
        // Questions come from other participants; in mic-only mode (in-person) everyone is on the mic.
        let isFromOthers = segment.source == .system || captureMode == .microphoneOnly
        guard isFromOthers else { return }

        if segment.isQuestion {
            if pendingQuestion == nil { pendingSince = Date() }
            if let pending = pendingQuestion {
                // Merge consecutive question fragments into one question.
                pendingQuestion = TranscriptSegment(
                    id: pending.id, source: pending.source, text: pending.text + " " + segment.text,
                    languageCode: segment.languageCode, startedAt: pending.startedAt, endedAt: segment.endedAt, isQuestion: true
                )
            } else {
                pendingQuestion = segment
            }
            scheduleAutomaticTrigger(endsWithQuestionMark: segment.text.trimmingCharacters(in: .whitespaces).hasSuffix("?"))
        } else if pendingQuestion != nil {
            // The participant is still talking — wait a little longer for the full question.
            scheduleAutomaticTrigger(endsWithQuestionMark: false)
        } else if let last = lastAutomatic,
                  segment.text.count >= 12,
                  segment.startedAt.timeIntervalSince(last.questionEnd) < Self.continuationWindow,
                  Date().timeIntervalSince(last.firedAt) < 8 {
            // The question continued after we already started answering: answer the full question.
            continueAutomaticAnswer(last, with: segment)
        }
    }

    private func continueAutomaticAnswer(_ last: AutomaticTrigger, with segment: TranscriptSegment) {
        let combined = last.questionText + " " + segment.text
        lastAutomatic = AutomaticTrigger(segmentID: last.segmentID, questionText: combined, questionEnd: segment.endedAt, firedAt: Date())
        let previous = suggestions.first { $0.triggerSegmentID == last.segmentID }
        automaticTask?.cancel()
        automaticTask = generate(
            kind: .answer,
            focusQuestion: combined,
            focusIsExplicit: false,
            triggerSegmentID: last.segmentID,
            automatic: true,
            replacing: previous
        )
    }

    private func scheduleAutomaticTrigger(endsWithQuestionMark: Bool) {
        debounceTask?.cancel()
        let since = pendingSince ?? Date()
        let wait = endsWithQuestionMark ? Self.questionMarkDebounce : Self.debounceInterval
        let fireAt = min(Date().addingTimeInterval(wait), since.addingTimeInterval(Self.maxDebounceWait))
        let delay = max(0, fireAt.timeIntervalSinceNow)
        guard delay > 0.01 else {
            fireAutomaticTrigger()
            return
        }
        debounceTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled else { return }
            self?.fireAutomaticTrigger()
        }
    }

    private func fireAutomaticTrigger() {
        guard let question = pendingQuestion else { return }
        pendingQuestion = nil
        pendingSince = nil

        if recentAutomaticQuestions.contains(where: { TextSimilarity.jaccard($0, question.text) >= Self.duplicateThreshold }) {
            Log.assistant.debug("Skipping duplicate question")
            return
        }
        recentAutomaticQuestions.append(question.text)
        if recentAutomaticQuestions.count > 12 { recentAutomaticQuestions.removeFirst() }
        lastAutomatic = AutomaticTrigger(segmentID: question.id, questionText: question.text, questionEnd: question.endedAt, firedAt: Date())

        automaticTask?.cancel()
        automaticTask = generate(
            kind: .answer,
            focusQuestion: question.text,
            focusIsExplicit: false,
            triggerSegmentID: question.id,
            automatic: true
        )
    }

    // MARK: Manual actions

    func whatShouldISay() {
        manual(.whatToSay) { $0.generate(kind: .whatToSay) }
    }

    func recap() {
        manual(.recap) { $0.generate(kind: .recap) }
    }

    func questionsToAsk() {
        manual(.questionsToAsk) { $0.generate(kind: .questionsToAsk) }
    }

    func answer(_ segment: TranscriptSegment) {
        manual(.answer) {
            $0.generate(kind: .answer, focusQuestion: segment.text, focusIsExplicit: true, triggerSegmentID: segment.id)
        }
    }

    func regenerate(_ suggestion: Suggestion) {
        manual(suggestion.kind) {
            $0.generate(
                kind: suggestion.kind,
                focusQuestion: suggestion.question,
                focusIsExplicit: suggestion.question != nil,
                triggerSegmentID: suggestion.triggerSegmentID,
                replacing: suggestion
            )
        }
    }

    func dismiss(_ suggestion: Suggestion) {
        suggestions.removeAll { $0.id == suggestion.id }
        if selectedSuggestionID == suggestion.id { selectedSuggestionID = nil }
    }

    func select(_ suggestion: Suggestion) {
        selectedSuggestionID = suggestion.id
    }

    func captureScreenContext() {
        guard !isCapturingScreen else { return }
        isCapturingScreen = true
        Task {
            defer { isCapturingScreen = false }
            do {
                pendingScreenContext = try await screenCapture.captureMainDisplay()
            } catch {
                lastError = error.localizedDescription
            }
        }
    }

    /// Clears suggestions for a new session.
    func reset() {
        debounceTask?.cancel()
        automaticTask?.cancel()
        manualTasks.values.forEach { $0.cancel() }
        manualTasks.removeAll()
        activeKinds.removeAll()
        pendingQuestion = nil
        pendingSince = nil
        recentAutomaticQuestions.removeAll()
        lastAutomatic = nil
        draft = nil
        draftRequestID = nil
        suggestions.removeAll()
        selectedSuggestionID = nil
        meetingTopic = nil
        lastError = nil
    }

    func restore(suggestions: [Suggestion], topic: String?) {
        self.suggestions = suggestions
        meetingTopic = topic
    }

    // MARK: Generation

    private func manual(_ kind: Suggestion.Kind, _ start: (SuggestionEngine) -> Task<Void, Never>?) {
        manualTasks[kind]?.cancel()
        manualTasks[kind] = start(self)
    }

    private func generate(
        kind: Suggestion.Kind,
        focusQuestion: String? = nil,
        focusIsExplicit: Bool = false,
        triggerSegmentID: UUID? = nil,
        automatic: Bool = false,
        replacing: Suggestion? = nil
    ) -> Task<Void, Never>? {
        guard keyProvider.hasKey else {
            lastError = OpenAIError.missingAPIKey.localizedDescription
            return nil
        }

        let budget = (kind == .recap) ? 24_000 : 3_000
        let request = AssistantRequest(
            kind: kind,
            focusQuestion: focusQuestion,
            focusIsExplicit: focusIsExplicit,
            transcript: transcript.recentLines(maxCharacters: budget),
            meetingTopic: meetingTopic,
            preferences: settings.assistantPreferences,
            model: settings.chatModel,
            screenContext: pendingScreenContext,
            previousAnswers: previousAnswers(excluding: replacing)
        )
        let usedScreen = request.screenContext != nil
        activeKinds.append(kind)
        Log.assistant.info("Requesting \(kind.rawValue, privacy: .public) (automatic: \(automatic))")

        let requestID = UUID()
        let streamsAnswer = kind == .answer || kind == .whatToSay
        let draftID = replacing?.id ?? UUID()
        let fallbackQuestion = focusQuestion
        if streamsAnswer {
            draftRequestID = requestID
            // Show the question the moment it is heard, before the first word of the answer arrives.
            if let fallbackQuestion, !fallbackQuestion.isEmpty {
                draft = Suggestion(id: draftID, kind: kind, question: fallbackQuestion, text: "",
                                   triggerSegmentID: triggerSegmentID, isAutomatic: automatic)
                selectedSuggestionID = nil
            } else {
                draft = nil
            }
        }

        return Task { [weak self] in
            defer { self?.finish(kind, requestID: requestID) }
            guard let self else { return }
            do {
                let output = try await self.assistant.generate(request) { [weak self] partial in
                    self?.updateDraft(partial, kind: kind, id: draftID, requestID: requestID, automatic: automatic,
                                      fallbackQuestion: fallbackQuestion, triggerSegmentID: triggerSegmentID)
                }
                try Task.checkCancellation()
                if usedScreen { self.pendingScreenContext = nil }
                self.accept(output, kind: kind, id: draftID, fallbackQuestion: fallbackQuestion, triggerSegmentID: triggerSegmentID, automatic: automatic, replacing: replacing, usedScreen: usedScreen)
            } catch is CancellationError {
                return
            } catch {
                guard !Task.isCancelled else { return }
                Log.assistant.error("Suggestion failed: \(error.localizedDescription, privacy: .public)")
                self.lastError = error.localizedDescription
            }
        }
    }

    private func finish(_ kind: Suggestion.Kind, requestID: UUID) {
        if let index = activeKinds.firstIndex(of: kind) { activeKinds.remove(at: index) }
        if draftRequestID == requestID {
            draft = nil
            draftRequestID = nil
        }
    }

    private func updateDraft(
        _ partial: AssistantPartial,
        kind: Suggestion.Kind,
        id: UUID,
        requestID: UUID,
        automatic: Bool,
        fallbackQuestion: String?,
        triggerSegmentID: UUID?
    ) {
        guard draftRequestID == requestID else { return }
        // For automatic triggers, only show something once the model confirms there is a question.
        if automatic && partial.questionDetected != true { return }
        let question = partial.question.flatMap { $0.isEmpty ? nil : $0 } ?? fallbackQuestion
        if draft == nil { selectedSuggestionID = nil }
        draft = Suggestion(
            id: id,
            kind: kind,
            question: question,
            text: partial.answer,
            explanation: partial.explanation.flatMap { $0.isEmpty ? nil : $0 },
            triggerSegmentID: triggerSegmentID,
            isAutomatic: automatic
        )
    }

    private func accept(
        _ output: AssistantOutput,
        kind: Suggestion.Kind,
        id: UUID,
        fallbackQuestion: String?,
        triggerSegmentID: UUID?,
        automatic: Bool,
        replacing: Suggestion?,
        usedScreen: Bool
    ) {
        if let topic = output.meetingTopic, !topic.isEmpty { meetingTopic = topic }
        lastError = nil

        // The model decided there was nothing to answer.
        if automatic && (!output.questionDetected || output.text.isEmpty) { return }

        let streamsAnswer = kind == .answer || kind == .whatToSay
        let question = output.question ?? (streamsAnswer ? fallbackQuestion : nil)
        var suggestion = Suggestion(
            id: id,
            kind: kind,
            question: question,
            text: output.text,
            explanation: output.explanation,
            languageName: output.language,
            confidence: output.confidence,
            needsMoreInfo: output.needsMoreInfo,
            directedAtUser: output.directedAtUser,
            items: output.items,
            actionItems: output.actionItems,
            openQuestions: output.openQuestions,
            usedScreenContext: usedScreen,
            triggerSegmentID: triggerSegmentID,
            isAutomatic: automatic
        )

        if let replacing, let index = suggestions.firstIndex(where: { $0.id == replacing.id }) {
            suggestion.id = replacing.id
            suggestions[index] = suggestion
            selectedSuggestionID = index == 0 ? nil : suggestion.id
            return
        }

        // An automatic answer to a question we already answered replaces the old card.
        if automatic, let question,
           let index = suggestions.firstIndex(where: {
               $0.kind == .answer && $0.question.map { TextSimilarity.jaccard($0, question) >= 0.8 } == true
           }) {
            suggestions.remove(at: index)
        }

        suggestions.insert(suggestion, at: 0)
        if suggestions.count > 60 { suggestions.removeLast(suggestions.count - 60) }
        selectedSuggestionID = nil
    }

    private func previousAnswers(excluding replacing: Suggestion?) -> [String] {
        var answers = suggestions
            .filter { $0.kind == .answer || $0.kind == .whatToSay }
            .prefix(4)
            .map(\.text)
        if let replacing, !replacing.text.isEmpty {
            answers.removeAll { $0 == replacing.text }
            answers.insert(replacing.text, at: 0)
        }
        return answers
    }
}
