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

    var errorDescription: String? {
        switch self {
        case .missingAPIKey:
            return "No OpenAI API key is configured. Add your key in Settings → OpenAI."
        case .http(let status, let message, let code, _):
            switch (status, code) {
            case (401, _):
                return "OpenAI rejected the API key (401). Check the key in Settings → OpenAI."
            case (_, "insufficient_quota"):
                return "Your OpenAI account has no available credit. Configure billing at platform.openai.com."
            case (429, _):
                return "OpenAI rate limit reached. Meetco will retry automatically. (\(message))"
            case (404, _):
                return "OpenAI could not find the requested model or endpoint (404). \(message)"
            default:
                return "OpenAI error \(status): \(message)"
            }
        case .network(let message):
            return "Network error: \(message)"
        case .decoding(let message):
            return "Unexpected response from OpenAI: \(message)"
        case .emptyResponse:
            return "OpenAI returned an empty response."
        }
    }
}
