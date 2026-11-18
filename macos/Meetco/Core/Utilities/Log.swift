import os

/// Central loggers. Never log API keys, request headers or transcript text.
enum Log {
    private static let subsystem = "io.github.meetco.Meetco"

    static let app = Logger(subsystem: subsystem, category: "app")
    static let audio = Logger(subsystem: subsystem, category: "audio")
    static let transcription = Logger(subsystem: subsystem, category: "transcription")
    static let assistant = Logger(subsystem: subsystem, category: "assistant")
    static let network = Logger(subsystem: subsystem, category: "network")
    static let storage = Logger(subsystem: subsystem, category: "storage")
}
