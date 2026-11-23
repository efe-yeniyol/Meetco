import Foundation
import Observation

/// Keeps `MeetingMemory` up to date in the background while the user listens.
///
/// Every time enough new transcript has arrived (or some time has passed), the new lines and the
/// current memory go to the assistant model, which returns the merged memory: who is in the meeting
/// (names, roles, organizations) and what was said (facts, decisions, summary). Answer, recap and
/// question requests then include it, so answers stay consistent with the whole meeting rather than
/// only the last few minutes of transcript. Updates never block or cancel answers.
@MainActor
@Observable
final class MeetingMemoryEngine {
    /// New transcript characters that trigger an update.
    static let updateThreshold = 350
    /// Lower threshold while the memory is still empty, so introductions are picked up quickly.
    static let firstUpdateThreshold = 120
    /// Any new transcript is processed after this long.
    static let maxInterval: TimeInterval = 45
    /// Wait after a failed update before trying again.
    static let retryDelay: TimeInterval = 30
    /// At most this much new transcript per update (the oldest pending lines are dropped first).
    static let maxCharactersPerUpdate = 8_000

    private(set) var memory = MeetingMemory()
    private(set) var isUpdating = false

    @ObservationIgnored private let assistant: any MeetingAssisting
    @ObservationIgnored private let transcript: TranscriptStore
    @ObservationIgnored private let settings: SettingsStore
    @ObservationIgnored private var processed: Set<UUID> = []
    @ObservationIgnored private var lastUpdate: Date?
    @ObservationIgnored private var lastFailure: Date?
    @ObservationIgnored private var task: Task<Void, Never>?
    /// Bumped on reset so a late result from the previous meeting is ignored.
    @ObservationIgnored private var generation = 0
    /// Receives the meeting topic the model reports.
    @ObservationIgnored var onTopic: ((String) -> Void)?

    init(assistant: any MeetingAssisting, transcript: TranscriptStore, settings: SettingsStore) {
        self.assistant = assistant
        self.transcript = transcript
        self.settings = settings
    }

    /// The memory to send with a request, or nil when the feature is off or nothing is known yet.
    var current: MeetingMemory? {
        settings.meetingMemoryEnabled && !memory.isEmpty ? memory : nil
    }

    /// Call after each new transcript segment; starts an update when one is due.
    func segmentAdded(now: Date = Date()) {
        guard settings.meetingMemoryEnabled, task == nil else { return }
        if let lastFailure, now.timeIntervalSince(lastFailure) < Self.retryDelay { return }
        let pending = transcript.segments.filter { !processed.contains($0.id) }
        let characters = pending.reduce(0) { $0 + $1.text.count }
        guard characters > 0 else { return }
        let threshold = memory.isEmpty ? Self.firstUpdateThreshold : Self.updateThreshold
        let intervalPassed = lastUpdate.map { now.timeIntervalSince($0) >= Self.maxInterval } ?? false
        guard characters >= threshold || intervalPassed else { return }
        update(with: pending)
    }

    func reset() {
        generation += 1
        task?.cancel()
        task = nil
        isUpdating = false
        memory = MeetingMemory()
        processed.removeAll()
        lastUpdate = nil
        lastFailure = nil
    }

    func restore(_ memory: MeetingMemory?, processing segments: [TranscriptSegment]) {
        reset()
        self.memory = memory ?? MeetingMemory()
        processed = Set(segments.map(\.id))
    }

    /// Waits for the running update (tests).
    func waitForUpdate() async {
        await task?.value
    }

    private func update(with pending: [TranscriptSegment]) {
        var lines: [TranscriptLine] = []
        var count = 0
        for segment in pending.reversed() {
            count += segment.text.count
            if count > Self.maxCharactersPerUpdate && !lines.isEmpty { break }
            lines.insert(TranscriptLine(segment), at: 0)
        }
        let ids = Set(pending.map(\.id))
        let request = AssistantRequest(
            kind: .memory,
            transcript: lines,
            preferences: settings.assistantPreferences,
            model: settings.assistantModel,
            provider: settings.assistantProvider,
            memory: memory
        )
        let generation = generation
        isUpdating = true
        Log.assistant.info("Updating meeting memory (\(lines.count) new lines)")
        task = Task { [weak self] in
            guard let self else { return }
            let output: AssistantOutput?
            do {
                output = try await self.assistant.generate(request) { _ in }
            } catch {
                if !(error is CancellationError) {
                    Log.assistant.error("Meeting memory update failed: \(error.localizedDescription, privacy: .public)")
                }
                output = nil
            }
            guard generation == self.generation else { return }
            self.task = nil
            self.isUpdating = false
            if let output, let memory = output.memory {
                self.memory = memory
                self.processed.formUnion(ids)
                self.lastUpdate = Date()
                self.lastFailure = nil
                if let topic = output.meetingTopic, !topic.isEmpty { self.onTopic?(topic) }
                // Lines that arrived during the update.
                self.segmentAdded()
            } else {
                self.lastFailure = Date()
            }
        }
    }
}
