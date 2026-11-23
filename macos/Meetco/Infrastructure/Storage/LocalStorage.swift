import Foundation

/// A finished listening session, stored locally only when the user enables it.
struct ArchivedSession: Codable, Sendable {
    var id = UUID()
    var startedAt: Date
    var endedAt: Date
    var meetingTopic: String?
    var segments: [TranscriptSegment]
    var suggestions: [Suggestion]
    /// People and key facts Meetco gathered during the meeting.
    var memory: MeetingMemory?
}

/// Saves transcripts as JSON in ~/Library/Application Support/Meetco/Sessions.
/// Audio is never written to disk.
final class SessionArchive: Sendable {
    let directory: URL

    init(directory: URL? = nil) {
        if let directory {
            self.directory = directory
        } else {
            let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
                ?? URL(fileURLWithPath: NSTemporaryDirectory())
            self.directory = base.appendingPathComponent("Meetco/Sessions", isDirectory: true)
        }
    }

    func save(_ session: ArchivedSession) throws {
        guard !session.segments.isEmpty else { return }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withFullDate, .withTime, .withColonSeparatorInTime]
        let name = "session-\(formatter.string(from: session.startedAt)).json"
            .replacingOccurrences(of: ":", with: "-")
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        try encoder.encode(session).write(to: directory.appendingPathComponent(name), options: [.atomic])
        Log.storage.info("Saved session transcript (\(session.segments.count) segments)")
    }

    func sessionCount() -> Int {
        (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil))?
            .filter { $0.pathExtension == "json" }.count ?? 0
    }

    func clearAll() throws {
        guard FileManager.default.fileExists(atPath: directory.path) else { return }
        for file in try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) {
            try FileManager.default.removeItem(at: file)
        }
        Log.storage.info("Cleared local session history")
    }
}
