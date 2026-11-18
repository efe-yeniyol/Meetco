import XCTest
@testable import Meetco

final class AudioChunkerTests: XCTestCase {
    private let sampleRate = 16_000

    private func tone(seconds: Double, amplitude: Float = 0.2) -> [Float] {
        let count = Int(Double(sampleRate) * seconds)
        return (0..<count).map { amplitude * sin(Float($0) * 2 * .pi * 220 / Float(sampleRate)) }
    }

    private func silence(seconds: Double) -> [Float] {
        [Float](repeating: 0, count: Int(Double(sampleRate) * seconds))
    }

    func testSilenceProducesNoChunks() {
        var chunker = AudioChunker(source: .system, configuration: AudioChunkerConfiguration())
        let chunks = chunker.append(silence(seconds: 5), at: Date())
        XCTAssertTrue(chunks.isEmpty)
        XCTAssertNil(chunker.flush(at: Date()))
    }

    func testSpeechFollowedByPauseEmitsOneChunk() {
        var chunker = AudioChunker(source: .microphone, configuration: AudioChunkerConfiguration())
        var chunks = chunker.append(tone(seconds: 2), at: Date())
        XCTAssertTrue(chunks.isEmpty, "No chunk until a pause")
        chunks = chunker.append(silence(seconds: 1.2), at: Date())
        XCTAssertEqual(chunks.count, 1)
        let chunk = try! XCTUnwrap(chunks.first)
        XCTAssertEqual(chunk.source, .microphone)
        XCTAssertGreaterThan(chunk.duration, 2.0)
        XCTAssertLessThan(chunk.duration, 2.9, "Trailing silence should be trimmed")
    }

    func testShortClickIsDiscarded() {
        var chunker = AudioChunker(source: .system, configuration: AudioChunkerConfiguration())
        _ = chunker.append(tone(seconds: 0.1), at: Date())
        let chunks = chunker.append(silence(seconds: 2), at: Date())
        XCTAssertTrue(chunks.isEmpty)
    }

    func testLongSpeechIsSplitAtMaximumDuration() {
        let configuration = AudioChunkerConfiguration(silenceToFlush: 0.8, minimumChunkDuration: 1, maximumChunkDuration: 5)
        var chunker = AudioChunker(source: .system, configuration: configuration)
        let chunks = chunker.append(tone(seconds: 12), at: Date())
        XCTAssertEqual(chunks.count, 2)
        XCTAssertEqual(chunks[0].duration, 5, accuracy: 0.05)
        XCTAssertTrue(chunker.hasActiveSpeech)
        XCTAssertNotNil(chunker.flush(at: Date()))
    }

    func testFlushEmitsPendingSpeech() {
        var chunker = AudioChunker(source: .system, configuration: AudioChunkerConfiguration())
        _ = chunker.append(tone(seconds: 1.5), at: Date())
        XCTAssertNotNil(chunker.flush(at: Date()))
        XCTAssertNil(chunker.flush(at: Date()))
    }
}
