@preconcurrency import AVFoundation
import Foundation
import Speech

/// Who turns speech into text.
enum TranscriptionProvider: String, CaseIterable, Identifiable, Codable, Sendable {
    /// OpenAI's speech-to-text models (live streaming or file uploads).
    case openAI
    /// Apple's on-device speech model (SpeechAnalyzer, macOS 26+). Audio never leaves the Mac.
    case onDevice

    var id: String { rawValue }

    var title: String {
        switch self {
        case .openAI: "OpenAI (cloud)"
        case .onDevice: "On this Mac (private, free)"
        }
    }

    /// Whether Apple's on-device transcriber can run on this Mac.
    static var isOnDeviceAvailable: Bool {
        if #available(macOS 26.0, *) { return SpeechTranscriber.isAvailable }
        return false
    }
}

enum OnDeviceTranscriptionError: LocalizedError, Equatable {
    case unavailable
    case unsupportedLanguage(String)
    case noAudioFormat

    var errorDescription: String? {
        switch self {
        case .unavailable:
            "On-device transcription needs macOS 26 or later on a supported Mac. Choose OpenAI in Settings → Audio."
        case .unsupportedLanguage(let language):
            "On-device transcription does not support \(language). Choose another spoken language or OpenAI in Settings → Audio."
        case .noAudioFormat:
            "The on-device speech model could not accept Meetco's audio."
        }
    }
}

/// Transcribes each utterance with Apple's on-device SpeechAnalyzer.
///
/// The speech model is downloaded once per language (by the system, shared with other apps) and
/// then runs fully offline. It needs a known language: "Detect automatically" uses the Mac's language.
struct OnDeviceTranscriptionService: TranscriptionServicing {
    private let engine: (any Sendable)?

    init() {
        if #available(macOS 26.0, *) { engine = OnDeviceTranscriptionEngine() } else { engine = nil }
    }

    func transcribe(_ chunk: AudioChunk, options: TranscriptionOptions) async throws -> TranscriptionResult {
        guard #available(macOS 26.0, *), let engine = engine as? OnDeviceTranscriptionEngine else {
            throw OnDeviceTranscriptionError.unavailable
        }
        let (text, locale) = try await engine.transcribe(samples: chunk.samples, sampleRate: chunk.sampleRate, languageCode: options.languageCode)
        return TranscriptionResult(text: text, languageCode: locale.language.languageCode?.identifier)
    }

    /// Downloads the speech model for the language ahead of the first utterance.
    func prepare(languageCode: String?) async throws {
        guard #available(macOS 26.0, *), let engine = engine as? OnDeviceTranscriptionEngine else {
            throw OnDeviceTranscriptionError.unavailable
        }
        _ = try await engine.locale(for: languageCode)
    }
}

@available(macOS 26.0, *)
actor OnDeviceTranscriptionEngine {
    /// Which Apple model handles a language. `SpeechTranscriber` is newer and better on long-form
    /// speech but covers fewer languages (no Turkish, for example); `DictationTranscriber` covers the rest.
    enum Recognizer: Sendable, Equatable { case speech, dictation }

    struct Resolved: Sendable, Equatable {
        let locale: Locale
        let recognizer: Recognizer
    }

    /// Requested language code ("" for automatic) → installed model.
    private var ready: [String: Resolved] = [:]

    func transcribe(samples: [Float], sampleRate: Int, languageCode: String?) async throws -> (String, Locale) {
        let resolved = try await prepare(languageCode)
        let module: any SpeechModule
        let collector: Task<String, Error>
        switch resolved.recognizer {
        case .speech:
            let transcriber = Self.speechTranscriber(resolved.locale)
            let results = transcriber.results
            module = transcriber
            collector = Task {
                var text = ""
                for try await result in results where result.isFinal { text += String(result.text.characters) }
                return text
            }
        case .dictation:
            let transcriber = Self.dictationTranscriber(resolved.locale)
            let results = transcriber.results
            module = transcriber
            collector = Task {
                var text = ""
                for try await result in results where result.isFinal { text += String(result.text.characters) }
                return text
            }
        }

        let analyzer = SpeechAnalyzer(modules: [module])
        guard let format = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [module]) else {
            collector.cancel()
            throw OnDeviceTranscriptionError.noAudioFormat
        }
        let buffer = try Self.buffer(samples: samples, sampleRate: sampleRate, format: format)
        let (input, continuation) = AsyncStream<AnalyzerInput>.makeStream()
        continuation.yield(AnalyzerInput(buffer: buffer))
        continuation.finish()
        do {
            _ = try await analyzer.analyzeSequence(input)
            try await analyzer.finalizeAndFinishThroughEndOfInput()
        } catch {
            collector.cancel()
            await analyzer.cancelAndFinishNow()
            throw error
        }
        let text = try await collector.value.trimmingCharacters(in: .whitespacesAndNewlines)
        return (text, resolved.locale)
    }

    func locale(for languageCode: String?) async throws -> Locale {
        try await prepare(languageCode).locale
    }

    /// Picks the model for the language and installs it if needed.
    func prepare(_ languageCode: String?) async throws -> Resolved {
        guard SpeechTranscriber.isAvailable else { throw OnDeviceTranscriptionError.unavailable }
        let key = languageCode ?? ""
        if let resolved = ready[key] { return resolved }

        let requested = languageCode.map { Locale(identifier: $0) } ?? Locale.current
        let resolved: Resolved
        let module: any SpeechModule
        // `supportedLocale(equivalentTo:)` also answers for languages the model can't download, so match the lists.
        if let locale = Self.bestMatch(for: requested, in: await SpeechTranscriber.supportedLocales) {
            resolved = Resolved(locale: locale, recognizer: .speech)
            module = Self.speechTranscriber(locale)
        } else if let locale = Self.bestMatch(for: requested, in: await DictationTranscriber.supportedLocales) {
            resolved = Resolved(locale: locale, recognizer: .dictation)
            module = Self.dictationTranscriber(locale)
        } else {
            let code = requested.language.languageCode?.identifier ?? key
            throw OnDeviceTranscriptionError.unsupportedLanguage(Locale(identifier: "en").localizedString(forLanguageCode: code) ?? code)
        }
        if let request = try await AssetInventory.assetInstallationRequest(supporting: [module]) {
            Log.transcription.info("Downloading on-device speech model for \(resolved.locale.identifier, privacy: .public)")
            try await request.downloadAndInstall()
        }
        ready[key] = resolved
        return resolved
    }

    /// Same language; the requested (or the Mac's) region when available, otherwise the most common variant.
    static func bestMatch(for requested: Locale, in supported: [Locale]) -> Locale? {
        guard let language = requested.language.languageCode else { return nil }
        let candidates = supported.filter { $0.language.languageCode == language }
        let region = requested.region ?? Locale.current.region
        if let region, let exact = candidates.first(where: { $0.region == region }) { return exact }
        let preferredRegions = ["US", "GB", "DE", "FR", "ES", "IT", "BR", "JP", "KR", "CN", "TR"]
        for preferred in preferredRegions {
            if let match = candidates.first(where: { $0.region?.identifier == preferred }) { return match }
        }
        return candidates.sorted { $0.identifier < $1.identifier }.first
    }

    private static func speechTranscriber(_ locale: Locale) -> SpeechTranscriber {
        SpeechTranscriber(locale: locale, preset: .transcription)
    }

    private static func dictationTranscriber(_ locale: Locale) -> DictationTranscriber {
        DictationTranscriber(locale: locale, contentHints: [], transcriptionOptions: [.punctuation], reportingOptions: [], attributeOptions: [])
    }

    /// 16 kHz mono Float32 samples converted to the analyzer's preferred format.
    static func buffer(samples: [Float], sampleRate: Int, format: AVAudioFormat) throws -> AVAudioPCMBuffer {
        guard let sourceFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: Double(sampleRate), channels: 1, interleaved: false),
              let source = AVAudioPCMBuffer(pcmFormat: sourceFormat, frameCapacity: AVAudioFrameCount(samples.count)),
              let channel = source.floatChannelData?[0] else {
            throw OnDeviceTranscriptionError.noAudioFormat
        }
        source.frameLength = AVAudioFrameCount(samples.count)
        samples.withUnsafeBufferPointer { channel.update(from: $0.baseAddress!, count: samples.count) }
        if format == sourceFormat { return source }

        guard let converter = AVAudioConverter(from: sourceFormat, to: format) else { throw OnDeviceTranscriptionError.noAudioFormat }
        let ratio = format.sampleRate / sourceFormat.sampleRate
        let capacity = AVAudioFrameCount(Double(samples.count) * ratio) + 1024
        guard let output = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: capacity) else { throw OnDeviceTranscriptionError.noAudioFormat }
        let feed = InputFeed()
        var conversionError: NSError?
        let status = converter.convert(to: output, error: &conversionError) { _, inputStatus in
            if feed.consumed {
                inputStatus.pointee = .endOfStream
                return nil
            }
            feed.consumed = true
            inputStatus.pointee = .haveData
            return source
        }
        if status == .error { throw conversionError ?? OnDeviceTranscriptionError.noAudioFormat }
        return output
    }
}

/// Sends each utterance to the transcription provider chosen in Settings.
struct TranscriptionRouter: TranscriptionServicing {
    let openAI: any TranscriptionServicing
    let onDevice: any TranscriptionServicing

    func transcribe(_ chunk: AudioChunk, options: TranscriptionOptions) async throws -> TranscriptionResult {
        switch options.provider {
        case .openAI: try await openAI.transcribe(chunk, options: options)
        case .onDevice: try await onDevice.transcribe(chunk, options: options)
        }
    }
}
