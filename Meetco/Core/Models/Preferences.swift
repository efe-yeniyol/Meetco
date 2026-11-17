import Foundation

enum ResponseLanguage: String, CaseIterable, Identifiable, Codable, Sendable {
    case auto, english, turkish, german, french, spanish

    var id: String { rawValue }

    var title: String {
        switch self {
        case .auto: "Match the speaker (automatic)"
        default: languageName ?? rawValue
        }
    }

    /// English name of the language, used in prompts. Nil for automatic.
    var languageName: String? {
        switch self {
        case .auto: nil
        case .english: "English"
        case .turkish: "Turkish"
        case .german: "German"
        case .french: "French"
        case .spanish: "Spanish"
        }
    }
}

enum ExplanationLanguage: String, CaseIterable, Identifiable, Codable, Sendable {
    case none, turkish, english

    var id: String { rawValue }

    var title: String {
        switch self {
        case .none: "Off"
        case .turkish: "Turkish"
        case .english: "English"
        }
    }

    var languageName: String? {
        switch self {
        case .none: nil
        case .turkish: "Turkish"
        case .english: "English"
        }
    }

    /// Short badge shown next to explanations.
    var badge: String {
        switch self {
        case .none: ""
        case .turkish: "TR"
        case .english: "EN"
        }
    }
}

enum ResponseStyle: String, CaseIterable, Identifiable, Codable, Sendable {
    case concise, detailed, formal, friendly, technical

    var id: String { rawValue }

    var title: String { rawValue.capitalized }

    var promptDescription: String {
        switch self {
        case .concise: "Concise: no filler and no repetition, short sentences that are easy to say out loud. Usually 2–5 sentences; take as many as the substance needs (a recipe or an explanation needs its ingredients or steps), never fewer."
        case .detailed: "Detailed: 3–6 sentences that use relevant specifics from the conversation."
        case .formal: "Formal, polished business tone in 2–4 sentences."
        case .friendly: "Warm, friendly and conversational; usually 2–5 sentences, with the actual substance included."
        case .technical: "Technically precise with correct terminology in 2–5 sentences."
        }
    }
}

enum AppearanceMode: String, CaseIterable, Identifiable, Codable, Sendable {
    case dark, light, system

    var id: String { rawValue }
    var title: String {
        switch self {
        case .dark: "Dark"
        case .light: "Light"
        case .system: "Match System"
        }
    }
}

enum PanelSize: String, CaseIterable, Identifiable, Codable, Sendable {
    case compact, expanded

    var id: String { rawValue }
    var title: String { rawValue.capitalized }
}

enum TranscriptionLanguage: String, CaseIterable, Identifiable, Codable, Sendable {
    case auto, english, turkish

    var id: String { rawValue }

    var title: String {
        switch self {
        case .auto: "Detect automatically"
        case .english: "English"
        case .turkish: "Turkish"
        }
    }

    /// ISO-639-1 code passed to the transcription API, nil for auto-detect.
    var isoCode: String? {
        switch self {
        case .auto: nil
        case .english: "en"
        case .turkish: "tr"
        }
    }
}

/// Trade-off between transcription latency and API usage.
enum LatencyProfile: String, CaseIterable, Identifiable, Codable, Sendable {
    case fast, balanced, economical

    var id: String { rawValue }
    var title: String { rawValue.capitalized }

    var detail: String {
        switch self {
        case .fast: "Sends speech as soon as the speaker pauses. Best for interviews. Recommended."
        case .balanced: "Waits for slightly longer pauses. Fewer API requests."
        case .economical: "Sends longer passages. Fewer requests, higher latency."
        }
    }

    var chunkerConfiguration: AudioChunkerConfiguration {
        switch self {
        case .fast: AudioChunkerConfiguration(silenceToFlush: 0.35, minimumChunkDuration: 0.5, maximumChunkDuration: 6)
        case .balanced: AudioChunkerConfiguration(silenceToFlush: 0.8, minimumChunkDuration: 1.2, maximumChunkDuration: 12)
        case .economical: AudioChunkerConfiguration(silenceToFlush: 1.2, minimumChunkDuration: 2.5, maximumChunkDuration: 25)
        }
    }
}

/// Model presets offered in Settings. Users can also type any model id.
enum ModelCatalog {
    struct Entry: Identifiable, Hashable, Sendable {
        let id: String
        let note: String
    }

    static let defaultAssistantModel = "gpt-6.1-terra"
    static let defaultTranscriptionModel = "gpt-live-transcribe"
    /// Defaults of earlier versions, upgraded automatically (see `SettingsStore`).
    static let supersededAssistantDefaults = ["gpt-4.1-mini"]
    static let supersededTranscriptionDefaults = ["gpt-4o-mini-transcribe"]

    static let assistantModels: [Entry] = [
        Entry(id: "gpt-6.1-terra", note: "Smart and fast, answers without thinking pauses (recommended)"),
        Entry(id: "gpt-6-luna", note: "Fastest, lower quality"),
        Entry(id: "gpt-6.1-sol", note: "Smarter, but always reasons first: slower to start"),
        Entry(id: "gpt-6-astra", note: "Most capable, slowest to start"),
        Entry(id: "gpt-4.1", note: "Previous generation"),
        Entry(id: "gpt-4.1-mini", note: "Previous generation, fast"),
    ]

    static let transcriptionModels: [Entry] = [
        Entry(id: "gpt-live-transcribe", note: "Live streaming, lowest latency (recommended)"),
        Entry(id: "gpt-transcribe", note: "Highest accuracy, uploads each phrase after it ends"),
        Entry(id: "gpt-4o-mini-transcribe", note: "Deprecated, shuts down Feb 26, 2027"),
    ]

    /// Streaming models that hear the audio over a live connection while it is being spoken.
    static func isLiveTranscriptionModel(_ model: String) -> Bool {
        model.lowercased().contains("live-transcribe")
    }

    /// The model used for file uploads (live models cannot transcribe files).
    static func fileTranscriptionModel(for model: String) -> String {
        isLiveTranscriptionModel(model) ? "gpt-transcribe" : model
    }

    /// `whisper-1` and the `gpt-4o-*-transcribe` family take `language`, `temperature` and
    /// `response_format`; newer models take a `languages` list and report detected languages.
    static func usesLegacyTranscriptionParameters(_ model: String) -> Bool {
        let id = model.lowercased()
        return id.hasPrefix("whisper") || id.hasPrefix("gpt-4o")
    }

    /// The lowest reasoning effort a model accepts: answers must start immediately.
    static func fastestReasoningEffort(for model: String) -> String? {
        guard isReasoningModel(model) else { return nil }
        let id = model.lowercased()
        if id.hasPrefix("gpt-6-astra") || id.hasPrefix("gpt-6.1-sol") || id.hasPrefix("o") { return "low" }
        // The first GPT-5 models (gpt-5, gpt-5-mini, gpt-5-nano) know "minimal" but not "none".
        if id.hasPrefix("gpt-5") && !id.hasPrefix("gpt-5.") { return "minimal" }
        return "none"
    }

    /// Reasoning models reject `temperature` and accept `reasoning_effort`.
    static func isReasoningModel(_ model: String) -> Bool {
        let id = model.lowercased()
        if id.contains("chat") { return false }
        return id.hasPrefix("gpt-5") || id.hasPrefix("gpt-6") || id.hasPrefix("o1") || id.hasPrefix("o3") || id.hasPrefix("o4")
    }
}
