import XCTest
@testable import Meetco

final class UtilityTests: XCTestCase {
    func testWAVHeader() {
        let data = WAVEncoder.encode(samples: [0, 0.5, -0.5, 1], sampleRate: 16_000, normalize: false)
        XCTAssertEqual(data.count, 44 + 8)
        XCTAssertEqual(String(data: data.prefix(4), encoding: .ascii), "RIFF")
        XCTAssertEqual(String(data: data[8..<12], encoding: .ascii), "WAVE")
        let sampleRate = data[24..<28].withUnsafeBytes { $0.loadUnaligned(as: UInt32.self) }
        XCTAssertEqual(UInt32(littleEndian: sampleRate), 16_000)
    }

    func testTextSimilarity() {
        XCTAssertGreaterThan(TextSimilarity.jaccard("How much does it cost?", "how much does it cost"), 0.99)
        XCTAssertLessThan(TextSimilarity.jaccard("How much does it cost?", "When can we start the pilot?"), 0.2)
    }

    func testHallucinationFilter() {
        XCTAssertTrue(TranscriptTextFilter.isLikelyHallucination("Thank you for watching!"))
        XCTAssertTrue(TranscriptTextFilter.isLikelyHallucination("Altyazı M.K."))
        XCTAssertFalse(TranscriptTextFilter.isLikelyHallucination("Thank you for the detailed overview of the roadmap."))
        XCTAssertEqual(TranscriptTextFilter.clean("[Music] Hello there  team"), "Hello there team")
    }

    func testLanguageDetection() {
        XCTAssertEqual(LanguageDetector.detect("Can you tell me more about the integration timeline for our team?"), "en")
        XCTAssertEqual(LanguageDetector.detect("Entegrasyon süreci hakkında biraz daha bilgi verebilir misiniz?"), "tr")
        XCTAssertEqual(LanguageDetector.code(forLanguageName: "turkish"), "tr")
    }

    func testReasoningModelDetection() {
        XCTAssertTrue(ModelCatalog.isReasoningModel("gpt-5-mini"))
        XCTAssertTrue(ModelCatalog.isReasoningModel("o4-mini"))
        XCTAssertFalse(ModelCatalog.isReasoningModel("gpt-4.1-mini"))
        XCTAssertFalse(ModelCatalog.isReasoningModel("gpt-5-chat-latest"))
    }

    func testBaseURLValidation() {
        XCTAssertNotNil(OpenAIConfiguration.validatedBaseURL("https://api.openai.com/v1"))
        XCTAssertNil(OpenAIConfiguration.validatedBaseURL("not a url"))
        XCTAssertNil(OpenAIConfiguration.validatedBaseURL("ftp://example.com"))
    }
}
