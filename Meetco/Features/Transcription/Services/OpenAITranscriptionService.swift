import Foundation

/// Sends audio chunks to OpenAI's `/audio/transcriptions` endpoint.
struct OpenAITranscriptionService: TranscriptionServicing {
    let client: any OpenAIClientProtocol

    func transcribe(_ chunk: AudioChunk, options: TranscriptionOptions) async throws -> TranscriptionResult {
        let wav = WAVEncoder.encode(samples: chunk.samples, sampleRate: chunk.sampleRate)
        // Only whisper-1 supports verbose_json (which reports the detected language).
        let verbose = options.model.hasPrefix("whisper")
        let filename = "chunk-\(chunk.sequence).wav"
        let response: TranscriptionResponse
        do {
            response = try await client.createTranscription(audio: wav, filename: filename, options: options, verbose: verbose)
        } catch OpenAIError.http(let status, _, _, _) where status == 400 && options.languageCode != nil {
            // The model rejected the language hint: let it detect the language itself.
            var relaxed = options
            relaxed.languageCode = nil
            response = try await client.createTranscription(audio: wav, filename: filename, options: relaxed, verbose: verbose)
        }
        let languageCode = response.language.flatMap(LanguageDetector.code(forLanguageName:))
            ?? response.languages?.first.map { String($0.code.prefix(2)).lowercased() }
        return TranscriptionResult(text: response.text, languageCode: languageCode)
    }
}
