import Foundation

enum LiveTranscriptionError: Error, Equatable {
    /// The live connection is not usable right now.
    case unavailable
    case timedOut
    case failed(String)
}

/// Converts 16 kHz audio to the 24 kHz the Realtime API expects (linear interpolation).
struct LinearUpsampler {
    private static let step = 16_000.0 / 24_000.0
    private var previous: Float = 0
    /// Position of the next output sample, in input-sample units of the next batch.
    private var position = 0.0

    mutating func process(_ input: [Float]) -> [Float] {
        guard let last = input.last else { return [] }
        var output: [Float] = []
        output.reserveCapacity(input.count * 3 / 2 + 2)
        let end = Double(input.count - 1)
        while position <= end {
            let index = Int(position.rounded(.down))
            let fraction = Float(position - Double(index))
            let left = index < 0 ? previous : input[index]
            let right = index + 1 < input.count ? input[index + 1] : left
            output.append(left + (right - left) * fraction)
            position += Self.step
        }
        position -= Double(input.count)
        previous = last
        return output
    }
}

/// Wire format of a Realtime API transcription session.
enum RealtimeTranscriptionWire {
    enum ServerEvent: Equatable {
        case committed(itemID: String)
        case delta(itemID: String, text: String)
        case completed(itemID: String, transcript: String)
        case failed(itemID: String, message: String)
        case error(String)
        case other
    }

    static let commit = #"{"type":"input_audio_buffer.commit"}"#
    static let clear = #"{"type":"input_audio_buffer.clear"}"#

    /// `https://api.openai.com/v1` → `wss://api.openai.com/v1/realtime?intent=transcription`
    static func url(baseURL: URL) -> URL? {
        guard var components = URLComponents(url: baseURL.appendingPathComponent("realtime"), resolvingAgainstBaseURL: false) else { return nil }
        components.scheme = components.scheme?.lowercased() == "http" ? "ws" : "wss"
        components.queryItems = [URLQueryItem(name: "intent", value: "transcription")]
        return components.url
    }

    static func sessionUpdate(model: String, languageCode: String?) -> String {
        var transcription: [String: Any] = ["model": model, "delay": "low"]
        if let languageCode { transcription["languages"] = [languageCode] }
        let event: [String: Any] = [
            "type": "session.update",
            "session": [
                "type": "transcription",
                "audio": [
                    "input": [
                        "format": ["type": "audio/pcm", "rate": 24_000],
                        "transcription": transcription,
                        // Meetco detects the end of speech itself and commits each utterance.
                        "turn_detection": NSNull(),
                    ] as [String: Any],
                ],
            ] as [String: Any],
        ]
        let data = (try? JSONSerialization.data(withJSONObject: event)) ?? Data()
        return String(decoding: data, as: UTF8.self)
    }

    /// `input_audio_buffer.append` with 24 kHz mono samples encoded as base64 PCM16.
    static func append(_ samples: [Float]) -> String {
        var pcm = Data(capacity: samples.count * 2)
        for sample in samples {
            let value = Int16(max(-1, min(1, sample)) * 32_767)
            withUnsafeBytes(of: value.littleEndian) { pcm.append(contentsOf: $0) }
        }
        return #"{"type":"input_audio_buffer.append","audio":""# + pcm.base64EncodedString() + #""}"#
    }

    static func parse(_ text: String) -> ServerEvent {
        guard let object = (try? JSONSerialization.jsonObject(with: Data(text.utf8))) as? [String: Any],
              let type = object["type"] as? String else { return .other }
        let itemID = object["item_id"] as? String
        switch type {
        case "input_audio_buffer.committed":
            return itemID.map { .committed(itemID: $0) } ?? .other
        case "conversation.item.input_audio_transcription.delta":
            guard let itemID, let delta = object["delta"] as? String else { return .other }
            return .delta(itemID: itemID, text: delta)
        case "conversation.item.input_audio_transcription.completed":
            guard let itemID else { return .other }
            return .completed(itemID: itemID, transcript: object["transcript"] as? String ?? "")
        case "conversation.item.input_audio_transcription.failed":
            guard let itemID else { return .other }
            return .failed(itemID: itemID, message: errorMessage(object))
        case "error":
            return .error(errorMessage(object))
        default:
            return .other
        }
    }

    private static func errorMessage(_ object: [String: Any]) -> String {
        (object["error"] as? [String: Any])?["message"] as? String ?? "Unknown error"
    }
}

/// One live transcription connection for one audio source.
///
/// Audio is streamed while the person is still speaking, so when Meetco detects the end of
/// the utterance the model has already heard it: the transcript comes back right after the
/// commit instead of after a file upload. Every failure is reported per utterance, and the
/// caller falls back to file transcription for that utterance.
final class RealtimeTranscriptionSession: LiveAudioSink {
    private let events: AsyncStream<LiveAudioEvent>.Continuation
    private let core: Core

    init(
        source: AudioSourceKind,
        model: String,
        languageCode: String?,
        keyProvider: APIKeyProvider,
        configuration: @escaping @Sendable () -> OpenAIConfiguration = { OpenAIConfiguration.current() }
    ) {
        let (stream, continuation) = AsyncStream.makeStream(of: LiveAudioEvent.self, bufferingPolicy: .unbounded)
        events = continuation
        let core = Core(source: source, model: model, languageCode: languageCode, keyProvider: keyProvider, configuration: configuration)
        self.core = core
        Task.detached(priority: .userInitiated) { await core.run(stream) }
    }

    func send(_ event: LiveAudioEvent) {
        events.yield(event)
    }

    /// The transcript of a committed utterance. Throws when the live stream could not provide it.
    func transcript(for chunkID: UUID, timeout: TimeInterval) async throws -> String {
        try await core.transcript(for: chunkID, timeout: timeout)
    }

    func close() {
        events.finish()
    }
}

private actor Core {
    private let source: AudioSourceKind
    private let model: String
    private let languageCode: String?
    private let keyProvider: APIKeyProvider
    private let configuration: @Sendable () -> OpenAIConfiguration
    private let urlSession = URLSession(configuration: .ephemeral)

    private var socket: URLSessionWebSocketTask?
    /// Identifies the current connection so late callbacks from an old one are ignored.
    private var generation = 0
    private var isReady = false
    private var isClosed = false
    /// Set after too many errors: every utterance then goes straight to file transcription.
    private var isDisabled = false
    /// True while the utterance in progress was only partly sent and must not be committed.
    private var skipUntilBoundary = false
    private var hasUncommittedAudio = false
    private var upsampler = LinearUpsampler()

    /// Chunks whose commit was sent, in order, waiting for the server to name their item.
    private var awaitingItemID: [UUID] = []
    private var chunkForItem: [String: UUID] = [:]
    private var results: [UUID: Result<String, LiveTranscriptionError>] = [:]
    private var waiters: [UUID: CheckedContinuation<String, Error>] = [:]
    private var abandoned: Set<UUID> = []

    private var failuresWithoutSuccess = 0
    private var reconnectTask: Task<Void, Never>?
    private var keepAliveTask: Task<Void, Never>?

    init(source: AudioSourceKind, model: String, languageCode: String?, keyProvider: APIKeyProvider,
         configuration: @escaping @Sendable () -> OpenAIConfiguration) {
        self.source = source
        self.model = model
        self.languageCode = languageCode
        self.keyProvider = keyProvider
        self.configuration = configuration
    }

    func run(_ stream: AsyncStream<LiveAudioEvent>) async {
        await connect()
        startKeepAlive()
        for await event in stream { await handle(event) }
        shutdown()
    }

    func transcript(for chunkID: UUID, timeout: TimeInterval) async throws -> String {
        if let result = results.removeValue(forKey: chunkID) { return try result.get() }
        if isClosed || isDisabled { throw LiveTranscriptionError.unavailable }
        return try await withCheckedThrowingContinuation { continuation in
            waiters[chunkID] = continuation
            Task { [weak self] in
                try? await Task.sleep(for: .seconds(timeout))
                await self?.expire(chunkID)
            }
        }
    }

    // MARK: Client events

    private func handle(_ event: LiveAudioEvent) async {
        switch event {
        case .audio(let samples):
            guard isReady, !skipUntilBoundary, let socket else {
                skipUntilBoundary = true
                return
            }
            let connection = generation
            do {
                try await socket.send(.string(RealtimeTranscriptionWire.append(upsampler.process(samples))))
                hasUncommittedAudio = true
            } catch {
                connectionLost(connection, reason: error.localizedDescription)
            }

        case .commit(let chunkID):
            defer { endUtterance() }
            guard isReady, !skipUntilBoundary, hasUncommittedAudio, let socket else {
                resolve(chunkID, .failure(.unavailable))
                return
            }
            let connection = generation
            awaitingItemID.append(chunkID)
            do {
                try await socket.send(.string(RealtimeTranscriptionWire.commit))
            } catch {
                connectionLost(connection, reason: error.localizedDescription)
            }

        case .discard:
            defer { endUtterance() }
            guard isReady, hasUncommittedAudio, let socket else { return }
            try? await socket.send(.string(RealtimeTranscriptionWire.clear))
        }
    }

    private func endUtterance() {
        skipUntilBoundary = false
        hasUncommittedAudio = false
        upsampler = LinearUpsampler()
    }

    // MARK: Server events

    private func handleServer(_ text: String, connection: Int) {
        guard connection == generation else { return }
        switch RealtimeTranscriptionWire.parse(text) {
        case .committed(let itemID):
            guard !awaitingItemID.isEmpty else { return }
            chunkForItem[itemID] = awaitingItemID.removeFirst()
        case .completed(let itemID, let transcript):
            guard let chunkID = chunkForItem.removeValue(forKey: itemID) else { return }
            failuresWithoutSuccess = 0
            resolve(chunkID, .success(transcript))
        case .failed(let itemID, let message):
            Log.transcription.error("Live transcription failed: \(message, privacy: .public)")
            guard let chunkID = chunkForItem.removeValue(forKey: itemID) else { return }
            resolve(chunkID, .failure(.failed(message)))
            noteFailure()
        case .error(let message):
            Log.transcription.error("Live transcription error: \(message, privacy: .public)")
            if !awaitingItemID.isEmpty { resolve(awaitingItemID.removeFirst(), .failure(.failed(message))) }
            noteFailure()
        case .delta, .other:
            break
        }
    }

    private func noteFailure() {
        failuresWithoutSuccess += 1
        guard failuresWithoutSuccess >= 4, !isDisabled else { return }
        // The account or endpoint does not support live transcription: stop trying.
        Log.transcription.notice("Live transcription disabled for this session; using file transcription")
        isDisabled = true
        disconnect()
        failEverything()
    }

    // MARK: Connection

    private func connect() async {
        guard !isClosed, !isDisabled, socket == nil else { return }
        guard let key = keyProvider.currentKey(), !key.isEmpty,
              let url = RealtimeTranscriptionWire.url(baseURL: configuration().baseURL) else { return }
        var request = URLRequest(url: url)
        request.timeoutInterval = 8
        request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")

        let task = urlSession.webSocketTask(with: request)
        generation += 1
        let connection = generation
        socket = task
        task.resume()
        receive(from: task, connection: connection)
        do {
            try await task.send(.string(RealtimeTranscriptionWire.sessionUpdate(model: model, languageCode: languageCode)))
            guard connection == generation, !isClosed else { return }
            isReady = true
            Log.transcription.info("Live transcription connected (\(self.source.rawValue, privacy: .public))")
        } catch {
            connectionLost(connection, reason: error.localizedDescription)
        }
    }

    private nonisolated func receive(from task: URLSessionWebSocketTask, connection: Int) {
        Task.detached(priority: .userInitiated) { [weak self] in
            while true {
                do {
                    let message = try await task.receive()
                    guard let self else { return }
                    switch message {
                    case .string(let text): await self.handleServer(text, connection: connection)
                    case .data(let data): await self.handleServer(String(decoding: data, as: UTF8.self), connection: connection)
                    @unknown default: break
                    }
                } catch {
                    await self?.connectionLost(connection, reason: error.localizedDescription)
                    return
                }
            }
        }
    }

    private func connectionLost(_ connection: Int, reason: String) {
        guard connection == generation, socket != nil else { return }
        Log.transcription.notice("Live transcription connection lost: \(reason, privacy: .public)")
        disconnect()
        failPending()
        // An utterance that was being streamed is incomplete on the next connection.
        skipUntilBoundary = true
        guard !isClosed, !isDisabled else { return }
        failuresWithoutSuccess += 1
        if failuresWithoutSuccess >= 6 {
            isDisabled = true
            failEverything()
            return
        }
        let delay = min(8, pow(2, Double(failuresWithoutSuccess - 1)) * 0.5)
        reconnectTask?.cancel()
        reconnectTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled else { return }
            await self?.connect()
        }
    }

    private func disconnect() {
        socket?.cancel(with: .normalClosure, reason: nil)
        socket = nil
        isReady = false
        hasUncommittedAudio = false
    }

    private func startKeepAlive() {
        keepAliveTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(20))
                await self?.ping()
            }
        }
    }

    private func ping() {
        guard isReady, let socket else { return }
        let connection = generation
        socket.sendPing { [weak self] error in
            guard let error else { return }
            Task { await self?.connectionLost(connection, reason: error.localizedDescription) }
        }
    }

    private func shutdown() {
        isClosed = true
        reconnectTask?.cancel()
        keepAliveTask?.cancel()
        disconnect()
        failEverything()
        urlSession.invalidateAndCancel()
    }

    // MARK: Results

    private func resolve(_ chunkID: UUID, _ result: Result<String, LiveTranscriptionError>) {
        if abandoned.remove(chunkID) != nil { return }
        if let waiter = waiters.removeValue(forKey: chunkID) {
            waiter.resume(with: result.mapError { $0 as Error })
        } else {
            results[chunkID] = result
        }
    }

    private func expire(_ chunkID: UUID) {
        guard let waiter = waiters.removeValue(forKey: chunkID) else { return }
        abandoned.insert(chunkID)
        waiter.resume(throwing: LiveTranscriptionError.timedOut)
    }

    /// Fails utterances that were committed on a connection that no longer exists.
    private func failPending() {
        let pending = awaitingItemID + Array(chunkForItem.values)
        awaitingItemID.removeAll()
        chunkForItem.removeAll()
        for chunkID in pending { resolve(chunkID, .failure(.unavailable)) }
    }

    private func failEverything() {
        failPending()
        let waiting = waiters
        waiters.removeAll()
        for waiter in waiting.values { waiter.resume(throwing: LiveTranscriptionError.unavailable) }
    }
}

/// Owns the live transcription connections of the current listening session (one per source).
final class LiveTranscriptionHub: @unchecked Sendable {
    private let keyProvider: APIKeyProvider
    private let lock = NSLock()
    private var sessions: [AudioSourceKind: RealtimeTranscriptionSession] = [:]

    init(keyProvider: APIKeyProvider) {
        self.keyProvider = keyProvider
    }

    func open(source: AudioSourceKind, model: String, languageCode: String?) -> any LiveAudioSink {
        let session = RealtimeTranscriptionSession(source: source, model: model, languageCode: languageCode, keyProvider: keyProvider)
        let previous = lock.withLock { sessions.updateValue(session, forKey: source) }
        previous?.close()
        return session
    }

    func session(for source: AudioSourceKind) -> RealtimeTranscriptionSession? {
        lock.withLock { sessions[source] }
    }

    func closeAll() {
        let closing = lock.withLock {
            defer { sessions.removeAll() }
            return Array(sessions.values)
        }
        closing.forEach { $0.close() }
    }
}

/// Transcribes with the live stream when it has the utterance, and with a file upload otherwise.
struct LiveFirstTranscriptionService: TranscriptionServicing {
    let hub: LiveTranscriptionHub
    let file: any TranscriptionServicing
    /// How long to wait for the live transcript after the utterance ended.
    var liveTimeout: TimeInterval = 3

    func transcribe(_ chunk: AudioChunk, options: TranscriptionOptions) async throws -> TranscriptionResult {
        if ModelCatalog.isLiveTranscriptionModel(options.model), let session = hub.session(for: chunk.source) {
            do {
                let text = try await session.transcript(for: chunk.id, timeout: liveTimeout)
                return TranscriptionResult(text: text, languageCode: nil)
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                Log.transcription.notice("Live transcript unavailable (\(String(describing: error), privacy: .public)); uploading the audio instead")
            }
        }
        var fileOptions = options
        fileOptions.model = ModelCatalog.fileTranscriptionModel(for: options.model)
        return try await file.transcribe(chunk, options: fileOptions)
    }
}
