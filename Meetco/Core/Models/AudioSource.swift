import Foundation

/// Where a piece of audio (and therefore a transcript segment) came from.
enum AudioSourceKind: String, Codable, Sendable, CaseIterable, Hashable {
    /// Audio played by the Mac (other meeting participants), captured with ScreenCaptureKit.
    case system
    /// The user's microphone, captured with AVAudioEngine.
    case microphone

    var speakerLabel: String {
        switch self {
        case .system: "Participants"
        case .microphone: "You"
        }
    }

    /// Label used inside prompts sent to the model.
    var promptLabel: String {
        switch self {
        case .system: "Participant"
        case .microphone: "You"
        }
    }

    var systemImage: String {
        switch self {
        case .system: "speaker.wave.2.fill"
        case .microphone: "mic.fill"
        }
    }
}

/// Which audio sources a listening session captures.
enum AudioCaptureMode: String, Codable, Sendable, CaseIterable, Identifiable {
    case systemAndMicrophone
    case systemOnly
    case microphoneOnly

    var id: String { rawValue }

    var title: String {
        switch self {
        case .systemAndMicrophone: "Microphone + System Audio"
        case .systemOnly: "System Audio Only"
        case .microphoneOnly: "Microphone Only"
        }
    }

    var shortTitle: String {
        switch self {
        case .systemAndMicrophone: "Mic + System"
        case .systemOnly: "System"
        case .microphoneOnly: "Mic"
        }
    }

    var systemImage: String {
        switch self {
        case .systemAndMicrophone: "waveform.badge.mic"
        case .systemOnly: "speaker.wave.2"
        case .microphoneOnly: "mic"
        }
    }

    var includesSystemAudio: Bool { self != .microphoneOnly }
    var includesMicrophone: Bool { self != .systemOnly }
}
