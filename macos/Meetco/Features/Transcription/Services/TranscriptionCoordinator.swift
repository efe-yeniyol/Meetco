import Foundation
import Observation

/// Turns audio chunks into transcript segments: calls the transcription service,
/// filters noise, detects language and questions, de-duplicates, and notifies listeners.
@MainActor
@Observable
final class TranscriptionCoordinator {
    private(set) var inFlightCount = 0
    private(set) var lastError: String?

    @ObservationIgnored var onSegment: ((TranscriptSegment) -> Void)?

    @ObservationIgnored private let service: any TranscriptionServicing
    @ObservationIgnored private let store: TranscriptStore
    @ObservationIgnored private let settings: SettingsStore
    @ObservationIgnored private let questionDetector = QuestionDetector()

    init(service: any TranscriptionServicing, store: TranscriptStore, settings: SettingsStore) {
        self.service = service
        self.store = store
        self.settings = settings
    }

    var isTranscribing: Bool { inFlightCount > 0 }

    func clearError() { lastError = nil }

    func process(_ chunk: AudioChunk) {
        let options = TranscriptionOptions(
            model: settings.transcriptionModel,
            languageCode: settings.transcriptionLanguage.isoCode,
            prompt: store.recentText(from: chunk.source, maxCharacters: 200)
        )
        inFlightCount += 1
        Log.transcription.debug("Transcribing \(chunk.source.rawValue, privacy: .public) chunk #\(chunk.sequence) (\(chunk.duration, format: .fixed(precision: 1))s)")

        Task {
            defer { inFlightCount -= 1 }
            do {
                let result = try await service.transcribe(chunk, options: options)
                lastError = nil
                accept(result, for: chunk)
            } catch is CancellationError {
                return
            } catch {
                Log.transcription.error("Transcription failed: \(error.localizedDescription, privacy: .public)")
                lastError = error.localizedDescription
            }
        }
    }

    /// Waits (bounded) for outstanding transcriptions, e.g. before archiving a session.
    func waitUntilIdle(timeout: TimeInterval) async {
        let deadline = Date().addingTimeInterval(timeout)
        while inFlightCount > 0 && Date() < deadline {
            try? await Task.sleep(for: .milliseconds(200))
        }
    }

    private func accept(_ result: TranscriptionResult, for chunk: AudioChunk) {
        let text = TranscriptTextFilter.clean(result.text)
        guard !text.isEmpty, !TranscriptTextFilter.isLikelyHallucination(text) else { return }

        // Avoid duplicate segments (e.g. a retried request or the model echoing its prompt).
        if let last = store.lastSegment(from: chunk.source),
           chunk.startedAt.timeIntervalSince(last.endedAt) < 20,
           TextSimilarity.normalized(last.text) == TextSimilarity.normalized(text) {
            return
        }

        let language = result.languageCode ?? LanguageDetector.detect(text)
        let segment = TranscriptSegment(
            source: chunk.source,
            text: text,
            languageCode: language,
            startedAt: chunk.startedAt,
            endedAt: chunk.endedAt,
            isQuestion: questionDetector.isQuestion(text)
        )
        store.insert(segment)
        onSegment?(segment)
    }
}
