import XCTest
@testable import Meetco

final class PartialJSONTests: XCTestCase {
    func testIncompleteString() {
        let text = #"{"question_detected": true, "question": "Why", "suggested_answer": "I think the main re"#
        let answer = PartialJSON.string(forKey: "suggested_answer", in: text)
        XCTAssertEqual(answer?.value, "I think the main re")
        XCTAssertEqual(answer?.isComplete, false)
        XCTAssertEqual(PartialJSON.string(forKey: "question", in: text)?.value, "Why")
        XCTAssertEqual(PartialJSON.bool(forKey: "question_detected", in: text), true)
    }

    func testEscapes() {
        let text = #"{"suggested_answer": "Say \"yes\"\nthen çok"}"#
        let answer = PartialJSON.string(forKey: "suggested_answer", in: text)
        XCTAssertEqual(answer?.value, "Say \"yes\"\nthen çok")
        XCTAssertEqual(answer?.isComplete, true)
    }

    func testDanglingEscapeIsIncomplete() {
        let text = #"{"suggested_answer": "abc\"#
        XCTAssertEqual(PartialJSON.string(forKey: "suggested_answer", in: text)?.value, "abc")
    }

    func testMissingKey() {
        XCTAssertNil(PartialJSON.string(forKey: "suggested_answer", in: #"{"question": "x""#))
        XCTAssertNil(PartialJSON.bool(forKey: "question_detected", in: #"{"question_detected": "#))
    }

    func testOrderedSerialization() {
        let value: JSONValue = ["b": 1, "a": ["x": "q\"uote"], "c": [true, .null]]
        XCTAssertEqual(value.serialized(), #"{"b":1,"a":{"x":"q\"uote"},"c":[true,null]}"#)
    }

    func testInterviewQuestions() {
        let detector = QuestionDetector()
        XCTAssertTrue(detector.isQuestion("Tell me about a time you handled a difficult stakeholder"))
        XCTAssertTrue(detector.isQuestion("I'd like to hear about your experience with Swift"))
        XCTAssertTrue(detector.isQuestion("Kendinizden biraz bahseder misiniz"))
    }
}
