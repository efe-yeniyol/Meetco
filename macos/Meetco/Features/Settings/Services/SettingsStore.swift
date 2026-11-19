import Foundation
import Observation

enum SettingsKeys {
    static let chatModel = "settings.chatModel"
    static let transcriptionModel = "settings.transcriptionModel"
    static let transcriptionProvider = "settings.transcriptionProvider"
    static let apiBaseURL = "settings.apiBaseURL"
    static let assistantProvider = "settings.assistantProvider"
    /// Model per non-OpenAI provider, keyed by `AssistantProvider.rawValue`.
    static let providerModels = "settings.providerModels"
    /// Base URL per non-OpenAI provider, keyed by `AssistantProvider.rawValue`.
    static let providerBaseURLs = "settings.providerBaseURLs"
    static let responseLanguage = "settings.responseLanguage"
    static let explanationLanguage = "settings.explanationLanguage"
    static let responseStyle = "settings.responseStyle"
    static let userContext = "settings.userContext"
    static let autoSuggest = "settings.autoSuggest"
    static let transcriptionLanguage = "settings.transcriptionLanguage"
    static let latencyProfile = "settings.latencyProfile"
    static let captureMode = "settings.captureMode"
    static let microphoneDeviceUID = "settings.microphoneDeviceUID"
    static let appearance = "settings.appearance"
    static let defaultPanelSize = "settings.defaultPanelSize"
    static let alwaysOnTop = "settings.alwaysOnTop"
    static let showOnAllSpaces = "settings.showOnAllSpaces"
    static let hideFromScreenSharing = "settings.hideFromScreenSharing"
    static let globalShortcutsEnabled = "settings.globalShortcutsEnabled"
    static let autoScrollTranscript = "settings.autoScrollTranscript"
    static let saveTranscriptsLocally = "settings.saveTranscriptsLocally"
    static let clearTranscriptOnNewSession = "settings.clearTranscriptOnNewSession"
    static let hasCompletedOnboarding = "settings.hasCompletedOnboarding"
}

/// User preferences persisted in UserDefaults. The API key is NOT stored here (see KeychainManager).
@MainActor
@Observable
final class SettingsStore {
    @ObservationIgnored private let defaults: UserDefaults

    // OpenAI
    var chatModel: String { didSet { defaults.set(chatModel, forKey: SettingsKeys.chatModel) } }
    var transcriptionModel: String { didSet { defaults.set(transcriptionModel, forKey: SettingsKeys.transcriptionModel) } }
    var transcriptionProvider: TranscriptionProvider { didSet { save(transcriptionProvider, SettingsKeys.transcriptionProvider) } }
    var apiBaseURL: String { didSet { defaults.set(apiBaseURL, forKey: SettingsKeys.apiBaseURL) } }

    // Assistant provider
    var assistantProvider: AssistantProvider { didSet { save(assistantProvider, SettingsKeys.assistantProvider) } }
    var providerModels: [String: String] { didSet { defaults.set(providerModels, forKey: SettingsKeys.providerModels) } }
    var providerBaseURLs: [String: String] { didSet { defaults.set(providerBaseURLs, forKey: SettingsKeys.providerBaseURLs) } }
    var responseLanguage: ResponseLanguage { didSet { save(responseLanguage, SettingsKeys.responseLanguage) } }
    var explanationLanguage: ExplanationLanguage { didSet { save(explanationLanguage, SettingsKeys.explanationLanguage) } }
    var responseStyle: ResponseStyle { didSet { save(responseStyle, SettingsKeys.responseStyle) } }
    var userContext: String { didSet { defaults.set(userContext, forKey: SettingsKeys.userContext) } }
    var autoSuggest: Bool { didSet { defaults.set(autoSuggest, forKey: SettingsKeys.autoSuggest) } }

    // Audio & transcription
    var transcriptionLanguage: TranscriptionLanguage { didSet { save(transcriptionLanguage, SettingsKeys.transcriptionLanguage) } }
    var latencyProfile: LatencyProfile { didSet { save(latencyProfile, SettingsKeys.latencyProfile) } }
    var captureMode: AudioCaptureMode { didSet { save(captureMode, SettingsKeys.captureMode) } }
    var microphoneDeviceUID: String? { didSet { defaults.set(microphoneDeviceUID, forKey: SettingsKeys.microphoneDeviceUID) } }

    // General
    var appearance: AppearanceMode { didSet { save(appearance, SettingsKeys.appearance) } }
    var defaultPanelSize: PanelSize { didSet { save(defaultPanelSize, SettingsKeys.defaultPanelSize) } }
    var alwaysOnTop: Bool { didSet { defaults.set(alwaysOnTop, forKey: SettingsKeys.alwaysOnTop) } }
    var showOnAllSpaces: Bool { didSet { defaults.set(showOnAllSpaces, forKey: SettingsKeys.showOnAllSpaces) } }
    var hideFromScreenSharing: Bool { didSet { defaults.set(hideFromScreenSharing, forKey: SettingsKeys.hideFromScreenSharing) } }
    var globalShortcutsEnabled: Bool { didSet { defaults.set(globalShortcutsEnabled, forKey: SettingsKeys.globalShortcutsEnabled) } }
    var autoScrollTranscript: Bool { didSet { defaults.set(autoScrollTranscript, forKey: SettingsKeys.autoScrollTranscript) } }
    var clearTranscriptOnNewSession: Bool { didSet { defaults.set(clearTranscriptOnNewSession, forKey: SettingsKeys.clearTranscriptOnNewSession) } }

    // Privacy
    var saveTranscriptsLocally: Bool { didSet { defaults.set(saveTranscriptsLocally, forKey: SettingsKeys.saveTranscriptsLocally) } }

    var hasCompletedOnboarding: Bool { didSet { defaults.set(hasCompletedOnboarding, forKey: SettingsKeys.hasCompletedOnboarding) } }

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        // Earlier defaults were saved the first time Settings was used; move them to the current ones.
        let savedChat = defaults.string(forKey: SettingsKeys.chatModel)
        chatModel = savedChat.flatMap { ModelCatalog.supersededAssistantDefaults.contains($0) ? nil : $0 } ?? ModelCatalog.defaultAssistantModel
        let savedTranscription = defaults.string(forKey: SettingsKeys.transcriptionModel)
        transcriptionModel = savedTranscription.flatMap { ModelCatalog.supersededTranscriptionDefaults.contains($0) ? nil : $0 } ?? ModelCatalog.defaultTranscriptionModel
        apiBaseURL = defaults.string(forKey: SettingsKeys.apiBaseURL) ?? OpenAIConfiguration.defaultBaseURL.absoluteString
        assistantProvider = Self.load(defaults, SettingsKeys.assistantProvider, .openAI)
        let savedTranscriptionProvider: TranscriptionProvider = Self.load(defaults, SettingsKeys.transcriptionProvider, .openAI)
        // A choice synced from a newer Mac falls back to OpenAI where the on-device model can't run.
        transcriptionProvider = savedTranscriptionProvider == .onDevice && !TranscriptionProvider.isOnDeviceAvailable ? .openAI : savedTranscriptionProvider
        providerModels = defaults.dictionary(forKey: SettingsKeys.providerModels) as? [String: String] ?? [:]
        providerBaseURLs = defaults.dictionary(forKey: SettingsKeys.providerBaseURLs) as? [String: String] ?? [:]
        responseLanguage = Self.load(defaults, SettingsKeys.responseLanguage, .auto)
        explanationLanguage = Self.load(defaults, SettingsKeys.explanationLanguage, .turkish)
        responseStyle = Self.load(defaults, SettingsKeys.responseStyle, .concise)
        userContext = defaults.string(forKey: SettingsKeys.userContext) ?? ""
        autoSuggest = defaults.object(forKey: SettingsKeys.autoSuggest) as? Bool ?? true
        transcriptionLanguage = Self.load(defaults, SettingsKeys.transcriptionLanguage, .auto)
        latencyProfile = Self.load(defaults, SettingsKeys.latencyProfile, .fast)
        captureMode = Self.load(defaults, SettingsKeys.captureMode, .systemAndMicrophone)
        microphoneDeviceUID = defaults.string(forKey: SettingsKeys.microphoneDeviceUID)
        appearance = Self.load(defaults, SettingsKeys.appearance, .dark)
        defaultPanelSize = Self.load(defaults, SettingsKeys.defaultPanelSize, .expanded)
        alwaysOnTop = defaults.object(forKey: SettingsKeys.alwaysOnTop) as? Bool ?? true
        showOnAllSpaces = defaults.object(forKey: SettingsKeys.showOnAllSpaces) as? Bool ?? true
        hideFromScreenSharing = defaults.object(forKey: SettingsKeys.hideFromScreenSharing) as? Bool ?? false
        globalShortcutsEnabled = defaults.object(forKey: SettingsKeys.globalShortcutsEnabled) as? Bool ?? true
        autoScrollTranscript = defaults.object(forKey: SettingsKeys.autoScrollTranscript) as? Bool ?? true
        clearTranscriptOnNewSession = defaults.object(forKey: SettingsKeys.clearTranscriptOnNewSession) as? Bool ?? true
        saveTranscriptsLocally = defaults.object(forKey: SettingsKeys.saveTranscriptsLocally) as? Bool ?? false
        hasCompletedOnboarding = defaults.bool(forKey: SettingsKeys.hasCompletedOnboarding)
    }

    /// Model used by `provider`. OpenAI keeps its original setting.
    func model(for provider: AssistantProvider) -> String {
        provider == .openAI ? chatModel : providerModels[provider.rawValue] ?? provider.defaultModel
    }

    func setModel(_ model: String, for provider: AssistantProvider) {
        if provider == .openAI { chatModel = model } else { providerModels[provider.rawValue] = model }
    }

    /// Base URL text for `provider`. OpenAI's is shared with transcription.
    func baseURL(for provider: AssistantProvider) -> String {
        provider == .openAI ? apiBaseURL : providerBaseURLs[provider.rawValue] ?? provider.defaultBaseURL.absoluteString
    }

    func setBaseURL(_ url: String, for provider: AssistantProvider) {
        if provider == .openAI { apiBaseURL = url } else { providerBaseURLs[provider.rawValue] = url }
    }

    /// The active assistant model.
    var assistantModel: String {
        get { model(for: assistantProvider) }
        set { setModel(newValue, for: assistantProvider) }
    }

    var assistantPreferences: AssistantPreferences {
        AssistantPreferences(
            responseLanguage: responseLanguage,
            explanationLanguage: explanationLanguage,
            style: responseStyle,
            userContext: userContext
        )
    }

    private func save<Value: RawRepresentable>(_ value: Value, _ key: String) where Value.RawValue == String {
        defaults.set(value.rawValue, forKey: key)
    }

    private static func load<Value: RawRepresentable>(_ defaults: UserDefaults, _ key: String, _ fallback: Value) -> Value
    where Value.RawValue == String {
        guard let raw = defaults.string(forKey: key), let value = Value(rawValue: raw) else { return fallback }
        return value
    }
}
