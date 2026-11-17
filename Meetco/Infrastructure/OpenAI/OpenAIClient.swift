import Foundation

/// Abstraction over the OpenAI REST API so services can be tested with fakes.
protocol OpenAIClientProtocol: Sendable {
    func createChatCompletion(_ request: ChatCompletionRequest) async throws -> ChatCompletionResponse
    /// Streams content deltas of a chat completion (server-sent events).
    func streamChatCompletion(_ request: ChatCompletionRequest) -> AsyncThrowingStream<String, Error>
    func createTranscription(audio: Data, filename: String, options: TranscriptionOptions, verbose: Bool) async throws -> TranscriptionResponse
    func listModels(apiKeyOverride: String?) async throws -> [String]
}

/// Thin, dependency-free OpenAI client built on URLSession.
///
/// - Uses the user's own API key from the Keychain (via `APIKeyProvider`).
/// - Retries transient failures (429, 5xx, network) with exponential backoff.
/// - Never logs credentials or request bodies.
final class OpenAIClient: OpenAIClientProtocol {
    struct RetryPolicy: Sendable {
        var maxRetries = 2
        var baseDelay: TimeInterval = 0.8
        var maxDelay: TimeInterval = 8
    }

    private let keyProvider: APIKeyProvider
    private let session: URLSession
    private let configuration: @Sendable () -> OpenAIConfiguration
    private let retryPolicy: RetryPolicy

    init(
        keyProvider: APIKeyProvider,
        configuration: @escaping @Sendable () -> OpenAIConfiguration = { OpenAIConfiguration.current() },
        retryPolicy: RetryPolicy = RetryPolicy()
    ) {
        self.keyProvider = keyProvider
        self.configuration = configuration
        self.retryPolicy = retryPolicy
        let sessionConfiguration = URLSessionConfiguration.ephemeral
        sessionConfiguration.timeoutIntervalForRequest = 60
        sessionConfiguration.timeoutIntervalForResource = 120
        sessionConfiguration.httpAdditionalHeaders = ["User-Agent": "Meetco/1.0 (macOS)"]
        sessionConfiguration.urlCache = nil
        self.session = URLSession(configuration: sessionConfiguration)
    }

    // MARK: Endpoints

    func createChatCompletion(_ request: ChatCompletionRequest) async throws -> ChatCompletionResponse {
        let body = try request.encodedBody()
        return try await send(path: "chat/completions", method: "POST", body: body, contentType: "application/json")
    }

    func streamChatCompletion(_ request: ChatCompletionRequest) -> AsyncThrowingStream<String, Error> {
        AsyncThrowingStream { continuation in
            let task = Task {
                do {
                    var streamingRequest = request
                    streamingRequest.stream = true
                    let urlRequest = try self.makeRequest(
                        path: "chat/completions",
                        method: "POST",
                        body: try streamingRequest.encodedBody(),
                        contentType: "application/json",
                        apiKeyOverride: nil
                    )
                    try await self.streamEvents(urlRequest, continuation: continuation)
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    /// Opens a connection to the API host ahead of time so the first real request is faster.
    func warmUp() async {
        guard keyProvider.hasKey else { return }
        _ = try? await listModels(apiKeyOverride: nil)
    }

    func createTranscription(
        audio: Data,
        filename: String,
        options: TranscriptionOptions,
        verbose: Bool
    ) async throws -> TranscriptionResponse {
        var form = MultipartFormData()
        form.addFile(name: "file", filename: filename, mimeType: "audio/wav", data: audio)
        form.addField(name: "model", value: options.model)
        if ModelCatalog.usesLegacyTranscriptionParameters(options.model) {
            form.addField(name: "response_format", value: verbose ? "verbose_json" : "json")
            form.addField(name: "temperature", value: "0")
            if let language = options.languageCode { form.addField(name: "language", value: language) }
        } else if let language = options.languageCode {
            form.addField(name: "languages[]", value: language)
        }
        if let prompt = options.prompt, !prompt.isEmpty { form.addField(name: "prompt", value: prompt) }
        return try await send(
            path: "audio/transcriptions",
            method: "POST",
            body: form.finalized(),
            contentType: form.contentType
        )
    }

    func listModels(apiKeyOverride: String? = nil) async throws -> [String] {
        let response: ModelListResponse = try await send(
            path: "models",
            method: "GET",
            body: nil,
            contentType: nil,
            apiKeyOverride: apiKeyOverride,
            retry: false
        )
        return response.data.map(\.id).sorted()
    }

    // MARK: Transport

    private func makeRequest(
        path: String,
        method: String,
        body: Data?,
        contentType: String?,
        apiKeyOverride: String?
    ) throws -> URLRequest {
        guard let apiKey = apiKeyOverride ?? keyProvider.currentKey(), !apiKey.isEmpty else {
            throw OpenAIError.missingAPIKey
        }
        let config = configuration()
        var request = URLRequest(url: config.baseURL.appendingPathComponent(path))
        request.httpMethod = method
        request.timeoutInterval = config.requestTimeout
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        if let contentType { request.setValue(contentType, forHTTPHeaderField: "Content-Type") }
        request.httpBody = body
        return request
    }

    /// Reads server-sent events and yields content deltas. Retries only before any content arrived.
    private func streamEvents(_ request: URLRequest, continuation: AsyncThrowingStream<String, Error>.Continuation) async throws {
        var attempt = 0
        while true {
            try Task.checkCancellation()
            var yieldedContent = false
            do {
                let (bytes, response): (URLSession.AsyncBytes, URLResponse)
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
                    throw Self.httpError(http, data: data, path: request.url?.path ?? "")
                }
                let decoder = JSONDecoder()
                for try await line in bytes.lines {
                    guard line.hasPrefix("data:") else { continue }
                    let payload = line.dropFirst(5).trimmingCharacters(in: .whitespaces)
                    if payload == "[DONE]" { return }
                    guard let chunk = try? decoder.decode(ChatCompletionChunk.self, from: Data(payload.utf8)),
                          let choice = chunk.choices.first else { continue }
                    if let refusal = choice.delta.refusal, !refusal.isEmpty {
                        throw OpenAIError.http(status: 200, message: "Refused: \(refusal)", code: "refusal", retryAfter: nil)
                    }
                    if let content = choice.delta.content, !content.isEmpty {
                        yieldedContent = true
                        continuation.yield(content)
                    }
                }
                return
            } catch let error as OpenAIError where error.isRetryable && !yieldedContent && attempt < retryPolicy.maxRetries {
                attempt += 1
                let delay = error.retryAfter.map { min($0, retryPolicy.maxDelay) } ?? retryPolicy.baseDelay * pow(2, Double(attempt - 1))
                try await Task.sleep(for: .seconds(delay))
            } catch let error as URLError {
                if error.code == .cancelled { throw CancellationError() }
                throw OpenAIError.network(error.localizedDescription)
            }
        }
    }

    private static func httpError(_ http: HTTPURLResponse, data: Data, path: String) -> OpenAIError {
        let envelope = try? JSONDecoder().decode(OpenAIErrorEnvelope.self, from: data)
        let message = envelope?.error.message ?? HTTPURLResponse.localizedString(forStatusCode: http.statusCode)
        let retryAfter = http.value(forHTTPHeaderField: "Retry-After").flatMap(TimeInterval.init)
        Log.network.error("HTTP \(http.statusCode) from \(path, privacy: .public)")
        return .http(status: http.statusCode, message: message, code: envelope?.error.code, retryAfter: retryAfter)
    }

    private func send<Response: Decodable>(
        path: String,
        method: String,
        body: Data?,
        contentType: String?,
        apiKeyOverride: String? = nil,
        retry: Bool = true
    ) async throws -> Response {
        let request = try makeRequest(path: path, method: method, body: body, contentType: contentType, apiKeyOverride: apiKeyOverride)

        var attempt = 0
        while true {
            try Task.checkCancellation()
            do {
                let data = try await perform(request)
                do {
                    return try JSONDecoder().decode(Response.self, from: data)
                } catch {
                    throw OpenAIError.decoding(String(describing: error))
                }
            } catch let error as OpenAIError where retry && error.isRetryable && attempt < retryPolicy.maxRetries {
                attempt += 1
                let backoff = min(retryPolicy.maxDelay, retryPolicy.baseDelay * pow(2, Double(attempt - 1)))
                let delay = error.retryAfter.map { min($0, retryPolicy.maxDelay) } ?? backoff * Double.random(in: 0.8...1.2)
                Log.network.notice("Retrying \(path, privacy: .public) (attempt \(attempt)) in \(delay, format: .fixed(precision: 1))s")
                try await Task.sleep(for: .seconds(delay))
            }
        }
    }

    private func perform(_ request: URLRequest) async throws -> Data {
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch let error as URLError {
            if error.code == .cancelled { throw CancellationError() }
            throw OpenAIError.network(error.localizedDescription)
        }

        guard let http = response as? HTTPURLResponse else {
            throw OpenAIError.network("Invalid response")
        }
        guard (200..<300).contains(http.statusCode) else {
            throw Self.httpError(http, data: data, path: request.url?.path ?? "")
        }
        return data
    }
}
