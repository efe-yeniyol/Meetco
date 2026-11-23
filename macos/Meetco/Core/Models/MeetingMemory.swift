import Foundation

/// A person in the meeting, as far as the conversation reveals them.
struct MeetingParticipant: Codable, Sendable, Hashable, Identifiable {
    /// The name as said in the meeting, or a short description when no name was said ("Customer's CTO").
    var name: String
    var role: String?
    var organization: String?
    /// What they care about, asked or said that matters (one short line).
    var notes: String?

    var id: String { name.lowercased() }

    /// "Product manager, Acme"
    var detail: String? {
        let parts = [role, organization].compactMap { $0?.isEmpty == false ? $0 : nil }
        return parts.isEmpty ? nil : parts.joined(separator: ", ")
    }
}

/// What Meetco remembers about the current meeting beyond the recent transcript: who is there and
/// what has been said. Updated in the background by `MeetingMemoryEngine` and given to every request.
struct MeetingMemory: Codable, Sendable, Equatable {
    /// The user's own name, if it was said in the meeting.
    var userName: String?
    var participants: [MeetingParticipant] = []
    /// Facts, numbers, decisions and commitments stated in the meeting, with who said them.
    var facts: [String] = []
    /// Short summary of the conversation so far.
    var summary: String = ""

    var isEmpty: Bool { userName == nil && participants.isEmpty && facts.isEmpty && summary.isEmpty }

    static let maxParticipants = 15
    static let maxFacts = 30

    /// Keeps the memory small enough to send with every answer request.
    func trimmed() -> MeetingMemory {
        var copy = self
        var seen = Set<String>()
        copy.participants = participants.filter { !$0.name.isEmpty && seen.insert($0.id).inserted }.prefix(Self.maxParticipants).map { $0 }
        copy.facts = Array(facts.filter { !$0.isEmpty }.suffix(Self.maxFacts))
        return copy
    }
}
