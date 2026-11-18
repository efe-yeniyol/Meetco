import Foundation
import Observation

/// Chronological, in-memory transcript for the current session.
@MainActor
@Observable
final class TranscriptStore {
    private(set) var segments: [TranscriptSegment] = []

    /// Inserts a segment keeping chronological order (chunks can finish out of order).
    func insert(_ segment: TranscriptSegment) {
        let index = segments.lastIndex(where: { $0.startedAt <= segment.startedAt }).map { $0 + 1 } ?? 0
        segments.insert(segment, at: index)
    }

    func clear() {
        segments.removeAll()
    }

    func lastSegment(from source: AudioSourceKind) -> TranscriptSegment? {
        segments.last { $0.source == source }
    }

    /// Most recent text from a source, used as a continuity prompt for transcription.
    func recentText(from source: AudioSourceKind, maxCharacters: Int) -> String {
        var collected: [String] = []
        var count = 0
        for segment in segments.reversed() where segment.source == source {
            collected.insert(segment.text, at: 0)
            count += segment.text.count
            if count >= maxCharacters { break }
        }
        return String(collected.joined(separator: " ").suffix(maxCharacters))
    }

    /// The most recent segments that fit within a character budget, oldest first.
    func recentLines(maxCharacters: Int) -> [TranscriptLine] {
        var lines: [TranscriptLine] = []
        var count = 0
        for segment in segments.reversed() {
            count += segment.text.count + 24
            if count > maxCharacters && !lines.isEmpty { break }
            lines.append(TranscriptLine(segment))
        }
        return lines.reversed()
    }

    var plainText: String {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"
        return segments.map { "[\(formatter.string(from: $0.startedAt))] \($0.source.speakerLabel): \($0.text)" }
            .joined(separator: "\n")
    }
}
