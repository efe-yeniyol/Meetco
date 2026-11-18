import XCTest
@testable import Meetco

final class ResponseParserTests: XCTestCase {
    func testParsesAnswer() throws {
        let json = """
        {"question_detected": true, "question": "Can your platform support 30 concurrent users?", "directed_at_user": true,
         "language": "English", "suggested_answer": "Yes, it can be configured for 30 concurrent users.",
         "explanation": "Evet, 30 eşzamanlı kullanıcı için yapılandırılabilir.", "confidence": 0.94,
         "needs_more_info": false, "meeting_topic": "Platform capacity"}
        """
        let output = try ResponseParser.parse(json, kind: .answer)
        XCTAssertTrue(output.questionDetected)
        XCTAssertEqual(output.language, "English")
        XCTAssertEqual(output.confidence ?? 0, 0.94, accuracy: 0.001)
        XCTAssertEqual(output.meetingTopic, "Platform capacity")
        XCTAssertNotNil(output.explanation)
    }

    func testAcceptsTurkishExplanationAliasAndCodeFence() throws {
        let content = """
        ```json
        {"question_detected": true, "question": "Q", "language": "English", "suggested_answer": "A",
         "turkish_explanation": "Açıklama", "confidence": "0.5"}
        ```
        """
        let output = try ResponseParser.parse(content, kind: .answer)
        XCTAssertEqual(output.explanation, "Açıklama")
        XCTAssertEqual(output.confidence, 0.5)
    }

    func testNoQuestionDetectedIsValid() throws {
        let output = try ResponseParser.parse(#"{"question_detected": false, "suggested_answer": ""}"#, kind: .answer)
        XCTAssertFalse(output.questionDetected)
        XCTAssertTrue(output.text.isEmpty)
    }

    func testMissingAnswerThrows() {
        XCTAssertThrowsError(try ResponseParser.parse(#"{"question_detected": true}"#, kind: .answer))
    }

    func testInvalidJSONThrows() {
        XCTAssertThrowsError(try ResponseParser.parse("Sure! Here's a recap.", kind: .recap))
        // Spoken answers are plain text, so prose is a valid answer.
        XCTAssertEqual(try ResponseParser.parse("Sure, I can do that.", kind: .answer).text, "Sure, I can do that.")
        XCTAssertThrowsError(try ResponseParser.parse("", kind: .answer))
    }

    func testParsesRecap() throws {
        let json = #"{"summary": "Discussed pricing.", "key_points": ["Pricing", "Timeline"], "action_items": ["Send quote"], "open_questions": [], "language": "English", "meeting_topic": "Sales"}"#
        let output = try ResponseParser.parse(json, kind: .recap)
        XCTAssertEqual(output.items.map(\.text), ["Pricing", "Timeline"])
        XCTAssertEqual(output.actionItems, ["Send quote"])
    }

    func testParsesQuestionsToAsk() throws {
        let json = #"{"questions": [{"question": "What is your timeline?", "explanation": "Zaman planınız nedir?"}, "Who else is involved?"], "language": "English", "meeting_topic": ""}"#
        let output = try ResponseParser.parse(json, kind: .questionsToAsk)
        XCTAssertEqual(output.items.count, 2)
        XCTAssertEqual(output.items[0].detail, "Zaman planınız nedir?")
    }
}
