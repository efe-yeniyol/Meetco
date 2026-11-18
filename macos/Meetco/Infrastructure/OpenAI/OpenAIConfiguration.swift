import Foundation

/// Network configuration for the OpenAI API. Never contains credentials.
struct OpenAIConfiguration: Sendable, Equatable {
    static let defaultBaseURL = URL(string: "https://api.openai.com/v1")!

    var baseURL: URL
    var requestTimeout: TimeInterval = 60

    /// Reads the user-configurable base URL from UserDefaults (thread-safe).
    static func current(defaults: UserDefaults = .standard) -> OpenAIConfiguration {
        let raw = defaults.string(forKey: SettingsKeys.apiBaseURL)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return OpenAIConfiguration(baseURL: validatedBaseURL(raw) ?? defaultBaseURL)
    }

    static func validatedBaseURL(_ raw: String) -> URL? {
        guard !raw.isEmpty, let url = URL(string: raw), let scheme = url.scheme?.lowercased(),
              scheme == "https" || scheme == "http", url.host != nil else { return nil }
        return url
    }
}
