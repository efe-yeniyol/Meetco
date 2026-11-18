import Foundation

/// Lists the models an endpoint offers; used by Settings to test a key.
protocol ModelListing: Sendable {
    func listModels(apiKeyOverride: String?) async throws -> [String]
}

protocol AnthropicClientProtocol: ModelListing {
    /// Streams the text deltas of a Messages API response.
    func streamMessage(_ request: AnthropicMessageRequest) -> AsyncThrowingStream<String, Error>
}

// MARK: - Wire types

struct AnthropicMessageRequest: Encodable, Sendable {
    struct Message: Encodable, Sendable {
        let role: String
        let content: [Block]
    }

    struct Block: Encodable, Sendable {
        struct Source: Encodable, Sendable {
            let type: String
            let mediaType: String
            let data: String

            enum CodingKeys: String, CodingKey {
                case type, data
                case mediaType = "media_type"
            }
        }

        let type: String
        var text: String?
        var source: Source?

        static func text(_ value: String) -> Block { Block(type: "text", text: value) }
        static func jpeg(_ data: Data) -> Block {
            Block(type: "image", source: Source(type: "base64", mediaType: "image/jpeg", data: data.base64EncodedString()))
        }
    }

    var model: String
    var system: String
    var messages: [Message]
    var maxTokens: Int
    var temperature: Double?
    var stream = true

    enum CodingKeys: String, CodingKey {
        case model, system, messages, temperature, stream
        case maxTokens = "max_tokens"
    }

    /// Converts OpenAI-style messages (system + user parts) to the Messages API shape.
    init(model: String, chatMessages: [ChatMessage], maxTokens: Int, temperature: Double?) {
        self.model = model
        self.maxTokens = maxTokens
        self.temperature = temperature
        var systemParts: [String] = []
        var messages: [Message] = []
        for message in chatMessages {
            let blocks: [Block]
            switch message.content {
            case .text(let text):
                blocks = [.text(text)]
            case .parts(let parts):
                blocks = parts.compactMap { part in
                    if let text = part.text { return .text(text) }
                    if let url = part.imageURL?.url, let data = Self.base64Payload(of: url) { return .jpeg(data) }
                    return nil
                }
            }
            if message.role == "system" {
                systemParts += blocks.compactMap(\.text)
            } else {
                messages.append(Message(role: message.role, content: blocks))
            }
        }
        self.system = systemParts.joined(separator: "\n\n")
        self.messages = messages
    }

    private static func base64Payload(of dataURL: String) -> Data? {
        guard let comma = dataURL.firstIndex(of: ",") else { return nil }
        return Data(base64Encoded: String(dataURL[dataURL.index(after: comma)...]))
    }
}

/// One server-sent event of a streamed Messages API response (only the fields Meetco reads).
private struct AnthropicStreamEvent: Decodable {
    struct Delta: Decodable {
        let type: String?
        let text: String?
    }
    struct ErrorBody: Decodable {
        let type: String?
        let message: String?
    }
    let type: String
    let delta: Delta?
    let error: ErrorBody?
}

// MARK: - Client

/// Minimal client for Anthropic's Messages API, built on URLSession like `OpenAIClient`.
/// Errors are reported as `OpenAIError` so retry and messaging behave the same for every provider.
final class AnthropicClient: AnthropicClientProtocol {
    static let apiVersion = "2023-06-01"

    private let keyProvider: APIKeyProvider
    private let configuration: @Sendable () -> OpenAIConfiguration
    private let session: URLSession
    private let maxRetries = 2

    init(
        keyProvider: APIKeyProvider,
        configuration: @escaping @Sendable () -> OpenAIConfiguration = { OpenAIConfiguration.current(for: .anthropic) }
    ) {
        self.keyProvider = keyProvider
        self.configuration = configuration
        let sessionConfiguration = URLSessionConfiguration.ephemeral
        sessionConfiguration.timeoutIntervalForRequest = 60
        sessionConfiguration.timeoutIntervalForResource = 180
        sessionConfiguration.httpAdditionalHeaders = ["User-Agent": "Meetco/1.0 (macOS)"]
        sessionConfiguration.urlCache = nil
        self.session = URLSession(configuration: sessionConfiguration)
    }

    func streamMessage(_ request: AnthropicMessageRequest) -> AsyncThrowingStream<String, Error> {
        AsyncThrowingStream { continuation in
            let task = Task {
                do {
                    let urlRequest = try self.makeRequest(path: "messages", method: "POST", body: JSONEncoder().encode(request), apiKeyOverride: nil)
                    try await self.streamEvents(urlRequest, continuation: continuation)
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    func listModels(apiKeyOverride: String?) async throws -> [String] {
        var request = try makeRequest(path: "models", method: "GET", body: nil, apiKeyOverride: apiKeyOverride)
        if let url = request.url, var components = URLComponents(url: url, resolvingAgainstBaseURL: false) {
            components.queryItems = [URLQueryItem(name: "limit", value: "100")]
            request.url = components.url
        }
        let (data, response) = try await data(for: request)
        guard (200..<300).contains(response.statusCode) else { throw Self.httpError(response, data: data) }
        do {
            return try JSONDecoder().decode(ModelListResponse.self, from: data).data.map(\.id).sorted()
        } catch {
            throw OpenAIError.decoding(String(describing: error))
        }
    }

    // MARK: Transport

    private func makeRequest(path: String, method: String, body: Data?, apiKeyOverride: String?) throws -> URLRequest {
        let apiKey = (apiKeyOverride ?? keyProvider.currentKey())?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard !apiKey.isEmpty else { throw OpenAIError.missingAPIKey }
        let config = configuration()
        var request = URLRequest(url: config.baseURL.appendingPathComponent(path))
        request.httpMethod = method
        request.timeoutInterval = config.requestTimeout
        request.setValue(apiKey, forHTTPHeaderField: "x-api-key")
        request.setValue(Self.apiVersion, forHTTPHeaderField: "anthropic-version")
        if let body {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = body
        }
        return request
    }

    private func data(for request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else { throw OpenAIError.network("Invalid response") }
            return (data, http)
        } catch let error as URLError {
            if error.code == .cancelled { throw CancellationError() }
            throw OpenAIError.network(error.localizedDescription)
        }
    }

    /// Reads server-sent events and yields text deltas. Retries only before any text arrived.
    private func streamEvents(_ request: URLRequest, continuation: AsyncThrowingStream<String, Error>.Continuation) async throws {
        var attempt = 0
        while true {
            try Task.checkCancellation()
            var yieldedContent = false
            do {
                let bytes: URLSession.AsyncBytes
                let response: URLResponse
                do {
                    (bytes, response) = try await session.bytes(for: request)
                } catch let error as URLError {
                    if error.code == .cancelled { throw CancellationError() }
                    throw OpenAIError.network(error.localizedDescription)
                }
                guard let http = response as? HTTPURLResponse else { throw OpenAIError.network("Invalid response") }
                guard (200..<300).contains(http.statusCode) else {
                    var data = Data()
                    for try await byte in bytes {
                        data.append(byte)
                        if data.count > 65_536 { break }
                    }
                    throw Self.httpError(http, data: data)
                }
                let decoder = JSONDecoder()
                for try await line in bytes.lines {
                    guard line.hasPrefix("data:") else { continue }
                    let payload = line.dropFirst(5).trimmingCharacters(in: .whitespaces)
                    guard let event = try? decoder.decode(AnthropicStreamEvent.self, from: Data(payload.utf8)) else { continue }
                    switch event.type {
                    case "content_block_delta":
                        if event.delta?.type == "text_delta", let text = event.delta?.text, !text.isEmpty {
                            yieldedContent = true
                            continuation.yield(text)
                        }
                    case "error":
                        // e.g. overloaded_error mid-stream; retryable only if nothing was shown yet.
                        let status = event.error?.type == "overloaded_error" ? 529 : 500
                        throw OpenAIError.http(status: status, message: event.error?.message ?? "Stream error", code: event.error?.type, retryAfter: nil)
                    case "message_stop":
                        return
                    default:
                        continue
                    }
                }
                return
            } catch let error as OpenAIError where error.isRetryable && !yieldedContent && attempt < maxRetries {
                attempt += 1
                let delay = error.retryAfter.map { min($0, 8) } ?? 0.8 * pow(2, Double(attempt - 1))
                try await Task.sleep(for: .seconds(delay))
            } catch let error as URLError {
                if error.code == .cancelled { throw CancellationError() }
                throw OpenAIError.network(error.localizedDescription)
            }
        }
    }

    private static func httpError(_ http: HTTPURLResponse, data: Data) -> OpenAIError {
        // Anthropic errors look like {"type":"error","error":{"type":"…","message":"…"}}.
        let envelope = try? JSONDecoder().decode(OpenAIErrorEnvelope.self, from: data)
        let message = envelope?.error.message ?? HTTPURLResponse.localizedString(forStatusCode: http.statusCode)
        let retryAfter = http.value(forHTTPHeaderField: "Retry-After").flatMap(TimeInterval.init)
        Log.network.error("HTTP \(http.statusCode) from Anthropic")
        return .http(status: http.statusCode, message: message, code: envelope?.error.type, retryAfter: retryAfter)
    }
}
