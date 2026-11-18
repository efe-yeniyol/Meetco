import Foundation

/// Network configuration for an OpenAI-compatible API. Never contains credentials.
struct OpenAIConfiguration: Sendable, Equatable {
    static let defaultBaseURL = URL(string: "https://api.openai.com/v1")!

    var baseURL: URL
    var requestTimeout: TimeInterval = 60

    /// Reads the user-configurable OpenAI base URL from UserDefaults (thread-safe).
    static func current(defaults: UserDefaults = .standard) -> OpenAIConfiguration {
        let raw = defaults.string(forKey: SettingsKeys.apiBaseURL)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return OpenAIConfiguration(baseURL: validatedBaseURL(raw) ?? defaultBaseURL)
    }

    /// Configuration of an assistant provider (thread-safe). OpenAI shares its URL with transcription.
    static func current(for provider: AssistantProvider, defaults: UserDefaults = .standard) -> OpenAIConfiguration {
        guard provider != .openAI else { return current(defaults: defaults) }
        let urls = defaults.dictionary(forKey: SettingsKeys.providerBaseURLs) as? [String: String] ?? [:]
        let raw = urls[provider.rawValue]?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        // Local models can take a while to load on the first request.
        return OpenAIConfiguration(baseURL: validatedBaseURL(raw) ?? provider.defaultBaseURL, requestTimeout: provider.isLocal ? 120 : 60)
    }

    static func validatedBaseURL(_ raw: String) -> URL? {
        guard !raw.isEmpty, let url = URL(string: raw), let scheme = url.scheme?.lowercased(),
              scheme == "https" || scheme == "http", url.host != nil else { return nil }
        return url
    }
}
