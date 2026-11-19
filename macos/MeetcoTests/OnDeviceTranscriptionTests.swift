import Speech
import XCTest
@testable import Meetco

/// Runs Apple's on-device speech model on speech synthesized with `say`.
///
/// Skipped where the model can't run (before macOS 26, unsupported hardware) or isn't installed yet.
/// Set `MEETCO_DOWNLOAD_SPEECH_MODELS=1` (`TEST_RUNNER_MEETCO_DOWNLOAD_SPEECH_MODELS=1` with
/// xcodebuild) to let the test download missing models.
final class OnDeviceTranscriptionTests: XCTestCase {
    func testTranscribesEnglishSpeech() async throws {
        let (text, elapsed) = try await transcribe("When can we launch the new product?", voice: "Samantha", language: "en")
        XCTAssertTrue(text.lowercased().contains("launch"), text)
        XCTAssertTrue(text.lowercased().contains("product"), text)
        print("On-device (en): \"\(text)\" in \(String(format: "%.2f", elapsed))s")
    }

    func testTranscribesTurkishSpeech() async throws {
        let (text, elapsed) = try await transcribe("Yeni ürünü ne zaman piyasaya sürebiliriz?", voice: "Yelda", language: "tr")
        XCTAssertTrue(text.lowercased().contains("ürün"), text)
        print("On-device (tr): \"\(text)\" in \(String(format: "%.2f", elapsed))s")
    }

    func testConvertsToAnalyzerFormat() throws {
        guard #available(macOS 26.0, *) else { throw XCTSkip("Needs macOS 26") }
        let format = try XCTUnwrap(AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 16_000, channels: 1, interleaved: true))
        let samples = (0..<16_000).map { sin(Float($0) * 0.05) * 0.5 }
        let buffer = try OnDeviceTranscriptionEngine.buffer(samples: samples, sampleRate: 16_000, format: format)
        XCTAssertEqual(buffer.format, format)
        XCTAssertEqual(Int(buffer.frameLength), 16_000, accuracy: 32)
    }

    func testPicksRecognizerByLanguage() async throws {
        guard #available(macOS 26.0, *) else { throw XCTSkip("Needs macOS 26") }
        let supported = [Locale(identifier: "en_GB"), Locale(identifier: "en_US"), Locale(identifier: "de_DE")]
        XCTAssertEqual(OnDeviceTranscriptionEngine.bestMatch(for: Locale(identifier: "en_GB"), in: supported)?.identifier, "en_GB")
        XCTAssertEqual(OnDeviceTranscriptionEngine.bestMatch(for: Locale(identifier: "de"), in: supported)?.identifier, "de_DE")
        XCTAssertNil(OnDeviceTranscriptionEngine.bestMatch(for: Locale(identifier: "tr"), in: supported))
    }

    func testRouterUsesChosenProvider() async throws {
        struct Fixed: TranscriptionServicing {
            let text: String
            func transcribe(_ chunk: AudioChunk, options: TranscriptionOptions) async throws -> TranscriptionResult {
                TranscriptionResult(text: text, languageCode: nil)
            }
        }
        let router = TranscriptionRouter(openAI: Fixed(text: "cloud"), onDevice: Fixed(text: "local"))
        let chunk = AudioChunk(source: .system, samples: [], sampleRate: 16_000, startedAt: Date(), endedAt: Date(), sequence: 0)
        let cloud = try await router.transcribe(chunk, options: TranscriptionOptions(model: "m", provider: .openAI))
        let local = try await router.transcribe(chunk, options: TranscriptionOptions(model: "m", provider: .onDevice))
        XCTAssertEqual(cloud.text, "cloud")
        XCTAssertEqual(local.text, "local")
    }

    // MARK: Helpers

    private func transcribe(_ phrase: String, voice: String, language: String) async throws -> (String, TimeInterval) {
        guard #available(macOS 26.0, *), SpeechTranscriber.isAvailable else { throw XCTSkip("On-device transcription is unavailable here") }
        let installedLocales = await SpeechTranscriber.installedLocales + DictationTranscriber.installedLocales
        let installed = installedLocales.contains { $0.language.languageCode?.identifier == language }
        let mayDownload = ProcessInfo.processInfo.environment["MEETCO_DOWNLOAD_SPEECH_MODELS"] == "1"
        guard installed || mayDownload else { throw XCTSkip("The \(language) speech model is not installed") }

        let samples = try synthesize(phrase, voice: voice)
        let service = OnDeviceTranscriptionService()
        try await service.prepare(languageCode: language)

        let chunk = AudioChunk(source: .system, samples: samples, sampleRate: 16_000, startedAt: Date(), endedAt: Date(), sequence: 0)
        let start = Date()
        let result = try await service.transcribe(chunk, options: TranscriptionOptions(model: "", languageCode: language, prompt: nil))
        XCTAssertEqual(result.languageCode, language)
        return (result.text, Date().timeIntervalSince(start))
    }

    /// 16 kHz mono Float32 speech rendered by the system voice.
    private func synthesize(_ phrase: String, voice: String) throws -> [Float] {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let aiff = directory.appendingPathComponent("speech.aiff")
        let wav = directory.appendingPathComponent("speech.wav")
        try run("/usr/bin/say", ["-v", voice, "-o", aiff.path, phrase])
        try run("/usr/bin/afconvert", ["-f", "WAVE", "-d", "LEF32@16000", "-c", "1", aiff.path, wav.path])

        let file = try AVAudioFile(forReading: wav)
        let buffer = try XCTUnwrap(AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)))
        try file.read(into: buffer)
        let channel = try XCTUnwrap(buffer.floatChannelData?[0])
        return Array(UnsafeBufferPointer(start: channel, count: Int(buffer.frameLength)))
    }

    private func run(_ tool: String, _ arguments: [String]) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = arguments
        try process.run()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw XCTSkip("\(tool) failed (\(process.terminationStatus))") }
    }
}
