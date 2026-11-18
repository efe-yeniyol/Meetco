import XCTest
@testable import Meetco

final class QuestionDetectorTests: XCTestCase {
    private let detector = QuestionDetector()

    func testEnglishQuestionMark() {
        XCTAssertTrue(detector.isQuestion("Can your platform support 30 concurrent users?"))
    }

    func testEnglishQuestionWithoutPunctuation() {
        XCTAssertTrue(detector.isQuestion("So how do you handle data retention for EU customers"))
        XCTAssertTrue(detector.isQuestion("Could you walk us through the pricing"))
    }

    func testEnglishStatementIsNotQuestion() {
        XCTAssertFalse(detector.isQuestion("We are planning to roll this out next quarter."))
        XCTAssertFalse(detector.isQuestion("I don't know what the timeline looks like."))
    }

    func testTurkishParticles() {
        XCTAssertTrue(detector.isQuestion("Bu sistemi yarın kurabilir misiniz"))
        XCTAssertTrue(detector.isQuestion("Fiyatlandırma aylık mı"))
    }

    func testTurkishQuestionWords() {
        XCTAssertTrue(detector.isQuestion("Entegrasyon ne kadar sürer"))
        XCTAssertTrue(detector.isQuestion("Bunu nasıl yapıyorsunuz"))
    }

    func testTurkishStatement() {
        XCTAssertFalse(detector.isQuestion("Toplantıyı yarın saat üçte yapalım."))
    }

    func testTooShort() {
        XCTAssertFalse(detector.isQuestion("Okay"))
    }
}
