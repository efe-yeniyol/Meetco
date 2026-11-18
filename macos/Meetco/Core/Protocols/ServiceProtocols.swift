import Foundation

/// Stores the user's OpenAI API key securely.
protocol APIKeyStoring: Sendable {
    func loadAPIKey() throws -> String?
    func saveAPIKey(_ key: String) throws
    func deleteAPIKey() throws
}

/// Options for a single transcription request.
struct TranscriptionOptions: Sendable, Equatable {
    var model: String
    /// ISO-639-1 language hint, nil for automatic detection.
    var languageCode: String?
    /// Preceding text that helps the model keep continuity.
    var prompt: String?
    var provider: TranscriptionProvider = .openAI
}

struct TranscriptionResult: Sendable, Equatable {
    var text: String
    var languageCode: String?
}

/// Converts captured audio chunks into text.
protocol TranscriptionServicing: Sendable {
    func transcribe(_ chunk: AudioChunk, options: TranscriptionOptions) async throws -> TranscriptionResult
}

/// Produces suggestions (answers, recaps, questions) from meeting context.
protocol MeetingAssisting: Sendable {
    /// Generates a suggestion, reporting partial answers while the model is still writing.
    func generate(
        _ request: AssistantRequest,
        onPartial: @escaping @MainActor @Sendable (AssistantPartial) -> Void
    ) async throws -> AssistantOutput
}

extension MeetingAssisting {
    func generate(_ request: AssistantRequest) async throws -> AssistantOutput {
        try await generate(request, onPartial: { _ in })
    }
}

/// Captures an image of the screen to give the assistant visual context.
protocol ScreenContextCapturing: Sendable {
    func captureMainDisplay() async throws -> ScreenContext
}
