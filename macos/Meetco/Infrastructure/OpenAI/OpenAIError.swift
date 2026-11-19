import Foundation

enum OpenAIError: LocalizedError, Sendable, Equatable {
    case missingAPIKey
    case http(status: Int, message: String, code: String?, retryAfter: TimeInterval?)
    case network(String)
    case decoding(String)
    case emptyResponse

    var isRetryable: Bool {
        switch self {
        case .http(let status, _, let code, _):
            if code == "insufficient_quota" { return false }
            return status == 408 || status == 409 || status == 429 || (500...599).contains(status)
        case .network:
            return true
        default:
            return false
        }
    }

    var retryAfter: TimeInterval? {
        if case .http(_, _, _, let retryAfter) = self { return retryAfter }
        return nil
    }

    var errorDescription: String? { description(service: "OpenAI", settingsTab: "OpenAI") }

    /// User-facing message naming the service that failed (the same errors come from every OpenAI-compatible provider).
    func description(service: String, settingsTab: String) -> String {
        switch self {
        case .missingAPIKey:
            return "No \(service) API key is configured. Add your key in Settings → \(settingsTab)."
        case .http(let status, let message, let code, _):
            switch (status, code) {
            case (401, _):
                return "\(service) rejected the API key (401). Check the key in Settings → \(settingsTab)."
            case (_, "insufficient_quota"):
                return "Your \(service) account has no available credit. Check billing in its dashboard."
            case (402, _):
                return "Your \(service) account has no available credit (402). \(message)"
            case (429, _):
                return "\(service) rate limit reached. Meetco will retry automatically. (\(message))"
            case (404, _):
                return "\(service) could not find the requested model or endpoint (404). \(message)"
            default:
                return "\(service) error \(status): \(message)"
            }
        case .network(let message):
            return "Network error reaching \(service): \(message)"
        case .decoding(let message):
            return "Unexpected response from \(service): \(message)"
        case .emptyResponse:
            return "\(service) returned an empty response."
        }
    }
}
