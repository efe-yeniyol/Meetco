import XCTest
@testable import Meetco

final class LiveTranscriptionTests: XCTestCase {
    private let sampleRate = 16_000

    private func tone(seconds: Double) -> [Float] {
        (0..<Int(Double(sampleRate) * seconds)).map { 0.2 * sin(Float($0) * 2 * .pi * 220 / Float(sampleRate)) }
    }

    private func silence(seconds: Double) -> [Float] {
        [Float](repeating: 0, count: Int(Double(sampleRate) * seconds))
    }

    func testChunkerStreamsUtteranceThenCommits() throws {
        var chunker = AudioChunker(source: .system, configuration: AudioChunkerConfiguration())
        chunker.forwardsLiveAudio = true
        XCTAssertTrue(chunker.append(silence(seconds: 1), at: Date()).isEmpty)
        XCTAssertTrue(chunker.drainLiveEvents().isEmpty, "Silence is never streamed")

        _ = chunker.append(tone(seconds: 1), at: Date())
        guard case .audio(let early)? = chunker.drainLiveEvents().first else { return XCTFail("Speech should stream before the utterance ends") }
        XCTAssertGreaterThan(early.count, sampleRate / 2)

        let chunks = chunker.append(silence(seconds: 1.2), at: Date())
        let chunk = try XCTUnwrap(chunks.first)
        XCTAssertEqual(chunker.drainLiveEvents().last, .commit(chunk.id))
    }

    func testChunkerDiscardsNoise() {
        var chunker = AudioChunker(source: .system, configuration: AudioChunkerConfiguration())
        chunker.forwardsLiveAudio = true
        _ = chunker.append(tone(seconds: 0.1) + silence(seconds: 1.5), at: Date())
        XCTAssertEqual(chunker.drainLiveEvents().last, .discard)
    }

    func testUpsamplerProducesThreeSamplesForEveryTwo() {
        var upsampler = LinearUpsampler()
        var total = 0
        for _ in 0..<10 { total += upsampler.process(tone(seconds: 0.0205)).count }
        let input = 10 * Int(Double(sampleRate) * 0.0205)
        XCTAssertLessThanOrEqual(abs(total - input * 3 / 2), 2)
        var ramp = LinearUpsampler()
        XCTAssertEqual(ramp.process([0, 3, 6, 9]), [0, 2, 4, 6, 8])
    }

    func testWireFormat() throws {
        XCTAssertEqual(
            RealtimeTranscriptionWire.url(baseURL: OpenAIConfiguration.defaultBaseURL)?.absoluteString,
            "wss://api.openai.com/v1/realtime?intent=transcription"
        )
        let update = RealtimeTranscriptionWire.sessionUpdate(model: "gpt-live-transcribe", languageCode: "tr")
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(update.utf8)) as? [String: Any])
        let session = try XCTUnwrap(object["session"] as? [String: Any])
        XCTAssertEqual(session["type"] as? String, "transcription")
        let input = try XCTUnwrap((session["audio"] as? [String: Any])?["input"] as? [String: Any])
        XCTAssertEqual((input["format"] as? [String: Any])?["rate"] as? Int, 24_000)
        XCTAssertEqual((input["transcription"] as? [String: Any])?["languages"] as? [String], ["tr"])
        XCTAssertTrue(input["turn_detection"] is NSNull)

        let append = RealtimeTranscriptionWire.append([0, 1, -1])
        let audio = try XCTUnwrap((JSONSerialization.jsonObject(with: Data(append.utf8)) as? [String: Any])?["audio"] as? String)
        XCTAssertEqual(Data(base64Encoded: audio), Data([0x00, 0x00, 0xFF, 0x7F, 0x01, 0x80]))
    }

    func testServerEvents() {
        XCTAssertEqual(RealtimeTranscriptionWire.parse(#"{"type":"input_audio_buffer.committed","item_id":"a"}"#), .committed(itemID: "a"))
        XCTAssertEqual(
            RealtimeTranscriptionWire.parse(#"{"type":"conversation.item.input_audio_transcription.completed","item_id":"a","content_index":0,"transcript":"Merhaba"}"#),
            .completed(itemID: "a", transcript: "Merhaba")
        )
        XCTAssertEqual(RealtimeTranscriptionWire.parse(#"{"type":"error","error":{"message":"bad"}}"#), .error("bad"))
        XCTAssertEqual(RealtimeTranscriptionWire.parse(#"{"type":"session.updated"}"#), .other)
    }

    func testFallsBackToFileModelWithoutLiveConnection() async throws {
        struct FileService: TranscriptionServicing {
            func transcribe(_ chunk: AudioChunk, options: TranscriptionOptions) async throws -> TranscriptionResult {
                TranscriptionResult(text: options.model, languageCode: nil)
            }
        }
        let hub = LiveTranscriptionHub(keyProvider: APIKeyProvider(store: EmptyKeyStore()))
        let service = LiveFirstTranscriptionService(hub: hub, file: FileService())
        let chunk = AudioChunk(source: .system, samples: [], sampleRate: 16_000, startedAt: Date(), endedAt: Date(), sequence: 1)
        let result = try await service.transcribe(chunk, options: TranscriptionOptions(model: "gpt-live-transcribe"))
        XCTAssertEqual(result.text, "gpt-transcribe")
    }
}

private struct EmptyKeyStore: APIKeyStoring {
    func loadAPIKey() throws -> String? { nil }
    func saveAPIKey(_ key: String) throws {}
    func deleteAPIKey() throws {}
}
