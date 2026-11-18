import Foundation
import Observation
@preconcurrency import ScreenCaptureKit

/// Owns a listening session: starts/stops audio capture, chunks audio per source,
/// forwards chunks to transcription and keeps UI-facing session state.
@MainActor
@Observable
final class MeetingSessionController {
    enum State: Equatable {
        case idle, starting, listening, stopping
    }

    private(set) var state: State = .idle
    private(set) var startedAt: Date?
    private(set) var activeMode: AudioCaptureMode?
    private(set) var activeSources: Set<AudioSourceKind> = []
    private(set) var systemLevel: Float = 0
    private(set) var microphoneLevel: Float = 0
    private(set) var errorMessage: String?
    private(set) var warningMessage: String?
    var isMicrophoneMuted = false {
        didSet {
            guard oldValue != isMicrophoneMuted else { return }
            let muted = isMicrophoneMuted
            if let processor = processors[.microphone] { Task { await processor.setMuted(muted) } }
            if muted { microphoneLevel = 0 }
        }
    }

    @ObservationIgnored private let settings: SettingsStore
    @ObservationIgnored private let permissions: PermissionsManager
    @ObservationIgnored private let keyProvider: APIKeyProvider
    @ObservationIgnored private let transcription: TranscriptionCoordinator
    @ObservationIgnored private let transcript: TranscriptStore
    @ObservationIgnored private let suggestions: SuggestionEngine
    @ObservationIgnored private let archive: SessionArchive
    @ObservationIgnored private let warmUp: @Sendable () async -> Void
    @ObservationIgnored private let liveTranscription: LiveTranscriptionHub?
    @ObservationIgnored private let onDeviceTranscription: OnDeviceTranscriptionService?

    @ObservationIgnored private let systemCapture = SystemAudioCaptureService()
    @ObservationIgnored private let microphoneCapture = MicrophoneCaptureService()
    @ObservationIgnored private var processors: [AudioSourceKind: ChunkingProcessor] = [:]
    @ObservationIgnored private var consumerTasks: [AudioSourceKind: Task<Void, Never>] = [:]
    @ObservationIgnored private var idleFlushTask: Task<Void, Never>?
    @ObservationIgnored private var systemRestartAttempts = 0

    init(
        settings: SettingsStore,
        permissions: PermissionsManager,
        keyProvider: APIKeyProvider,
        transcription: TranscriptionCoordinator,
        transcript: TranscriptStore,
        suggestions: SuggestionEngine,
        archive: SessionArchive,
        liveTranscription: LiveTranscriptionHub? = nil,
        onDeviceTranscription: OnDeviceTranscriptionService? = nil,
        warmUp: @escaping @Sendable () async -> Void = {}
    ) {
        self.warmUp = warmUp
        self.liveTranscription = liveTranscription
        self.onDeviceTranscription = onDeviceTranscription
        self.settings = settings
        self.permissions = permissions
        self.keyProvider = keyProvider
        self.transcription = transcription
        self.transcript = transcript
        self.suggestions = suggestions
        self.archive = archive

        transcription.onSegment = { [weak self] segment in
            guard let self else { return }
            self.suggestions.handleNewSegment(segment, captureMode: self.activeMode ?? self.settings.captureMode)
        }
    }

    var isListening: Bool { state == .listening }
    var isBusy: Bool { state == .starting || state == .stopping }

    // MARK: Public controls

    func toggleListening() {
        switch state {
        case .idle: start()
        case .listening: stop()
        default: break
        }
    }

    func start() {
        guard state == .idle else { return }
        Task { await startSession(mode: settings.captureMode, isRestart: false) }
    }

    func stop() {
        guard state == .listening || state == .starting else { return }
        Task { await stopSession(archive: true) }
    }

    /// Changes the capture mode; restarts capture if a session is running.
    func setCaptureMode(_ mode: AudioCaptureMode) {
        settings.captureMode = mode
        guard state == .listening else { return }
        Task {
            await stopSession(archive: false)
            await startSession(mode: mode, isRestart: true)
        }
    }

    func dismissMessages() {
        errorMessage = nil
        warningMessage = nil
    }

    /// Synchronous best-effort teardown used at app termination.
    func shutdown() {
        consumerTasks.values.forEach { $0.cancel() }
        idleFlushTask?.cancel()
        liveTranscription?.closeAll()
        microphoneCapture.stop()
        let system = systemCapture
        Task.detached { await system.stop() }
    }

    // MARK: Session lifecycle

    private func startSession(mode: AudioCaptureMode, isRestart: Bool) async {
        errorMessage = nil
        warningMessage = nil
        switch settings.transcriptionProvider {
        case .openAI:
            guard keyProvider.hasKey else {
                errorMessage = "Add your OpenAI API key in Settings, or choose on-device transcription in Settings → Audio, before you start listening."
                return
            }
        case .onDevice:
            guard TranscriptionProvider.isOnDeviceAvailable else {
                errorMessage = OnDeviceTranscriptionError.unavailable.localizedDescription
                return
            }
            // Downloads the speech model the first time a language is used (shared with other apps).
            if let onDeviceTranscription {
                let language = settings.transcriptionLanguage.isoCode
                Task { [weak self] in
                    do {
                        try await onDeviceTranscription.prepare(languageCode: language)
                    } catch {
                        self?.errorMessage = error.localizedDescription
                    }
                }
            }
        }
        state = .starting
        // Open the HTTPS connection now so the first transcription/answer doesn't pay for the TLS handshake.
        let warmUp = self.warmUp
        Task.detached(priority: .utility) { await warmUp() }

        if !isRestart && settings.clearTranscriptOnNewSession {
            transcript.clear()
            suggestions.reset()
        }

        let configuration = settings.latencyProfile.chunkerConfiguration
        var problems: [String] = []

        if mode.includesMicrophone {
            if await permissions.ensureMicrophone() {
                do {
                    let stream = try microphoneCapture.start(deviceUID: settings.microphoneDeviceUID)
                    attach(stream, source: .microphone, configuration: configuration)
                } catch {
                    problems.append(error.localizedDescription)
                }
            } else {
                problems.append(MicrophoneCaptureError.permissionDenied.localizedDescription)
            }
        }

        if mode.includesSystemAudio {
            do {
                let stream = try await systemCapture.start(onInterruption: { [weak self] error in
                    Task { @MainActor in self?.handleSystemInterruption(error) }
                })
                attach(stream, source: .system, configuration: configuration)
            } catch {
                problems.append(error.localizedDescription)
            }
            permissions.refresh()
        }

        guard !activeSources.isEmpty else {
            errorMessage = problems.joined(separator: "\n")
            state = .idle
            return
        }
        if !problems.isEmpty { warningMessage = problems.joined(separator: "\n") }

        if !isRestart || startedAt == nil { startedAt = Date() }
        activeMode = mode
        systemRestartAttempts = 0
        state = .listening
        startIdleFlushLoop()
        Log.audio.info("Listening started (\(mode.rawValue, privacy: .public))")
    }

    private func stopSession(archive shouldArchive: Bool) async {
        state = .stopping
        idleFlushTask?.cancel()
        idleFlushTask = nil

        microphoneCapture.stop()
        await systemCapture.stop()

        // Flush the last utterance from each source before tearing down.
        for (_, processor) in processors {
            if let chunk = await processor.finish(now: Date()) { transcription.process(chunk) }
        }
        consumerTasks.values.forEach { $0.cancel() }
        consumerTasks.removeAll()
        processors.removeAll()
        // Utterances still waiting on a live transcript fall back to a file upload.
        liveTranscription?.closeAll()
        activeSources.removeAll()
        systemLevel = 0
        microphoneLevel = 0

        let sessionStart = startedAt
        state = .idle
        if shouldArchive {
            startedAt = nil
            activeMode = nil
            if settings.saveTranscriptsLocally, let sessionStart {
                await transcription.waitUntilIdle(timeout: 15)
                saveArchive(startedAt: sessionStart)
            }
        }
        Log.audio.info("Listening stopped")
    }

    private func saveArchive(startedAt: Date) {
        let session = ArchivedSession(
            startedAt: startedAt,
            endedAt: Date(),
            meetingTopic: suggestions.meetingTopic,
            segments: transcript.segments,
            suggestions: suggestions.suggestions
        )
        do {
            try archive.save(session)
        } catch {
            Log.storage.error("Could not save transcript: \(error.localizedDescription, privacy: .public)")
        }
    }

    // MARK: Audio plumbing

    private func attach(_ stream: AsyncStream<[Float]>, source: AudioSourceKind, configuration: AudioChunkerConfiguration) {
        consumerTasks[source]?.cancel()
        // With a live model the audio is streamed while it is spoken; chunks only mark utterance ends.
        var liveSink: (any LiveAudioSink)?
        if settings.transcriptionProvider == .openAI && ModelCatalog.isLiveTranscriptionModel(settings.transcriptionModel) {
            liveSink = liveTranscription?.open(
                source: source,
                model: settings.transcriptionModel,
                languageCode: settings.transcriptionLanguage.isoCode
            )
        }
        let processor = ChunkingProcessor(source: source, configuration: configuration, liveSink: liveSink)
        processors[source] = processor
        activeSources.insert(source)
        if source == .microphone && isMicrophoneMuted {
            Task { await processor.setMuted(true) }
        }

        consumerTasks[source] = Task.detached(priority: .userInitiated) { [weak self] in
            var lastLevelUpdate = Date.distantPast
            for await samples in stream {
                if Task.isCancelled { break }
                let now = Date()
                let output = await processor.ingest(samples, at: now)
                for chunk in output.chunks {
                    await self?.handle(chunk)
                }
                if now.timeIntervalSince(lastLevelUpdate) >= 0.08 {
                    lastLevelUpdate = now
                    await self?.updateLevel(output.level, for: source)
                }
            }
            await self?.sourceEnded(source)
        }
    }

    private func handle(_ chunk: AudioChunk) {
        transcription.process(chunk)
    }

    private func updateLevel(_ level: Float, for source: AudioSourceKind) {
        switch source {
        case .system: systemLevel = level
        case .microphone: microphoneLevel = isMicrophoneMuted ? 0 : level
        }
    }

    private func sourceEnded(_ source: AudioSourceKind) {
        updateLevel(0, for: source)
    }

    private func startIdleFlushLoop() {
        idleFlushTask?.cancel()
        idleFlushTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(150))
                guard let self, !Task.isCancelled else { return }
                for (_, processor) in self.processors {
                    if let chunk = await processor.flushIfIdle(now: Date()) {
                        self.transcription.process(chunk)
                    }
                }
            }
        }
    }

    private func handleSystemInterruption(_ error: Error) {
        guard state == .listening else { return }
        activeSources.remove(.system)
        systemLevel = 0

        let nsError = error as NSError
        if nsError.domain == SCStreamErrorDomain && nsError.code == SCStreamError.userStopped.rawValue {
            warningMessage = "System audio capture was stopped from the macOS menu bar. Stop and start listening to resume."
            return
        }
        guard systemRestartAttempts < 3 else {
            warningMessage = "System audio capture was interrupted: \(error.localizedDescription)"
            return
        }
        systemRestartAttempts += 1
        warningMessage = "System audio was interrupted — reconnecting…"
        Task {
            try? await Task.sleep(for: .seconds(1))
            guard state == .listening else { return }
            do {
                let stream = try await systemCapture.start(onInterruption: { [weak self] error in
                    Task { @MainActor in self?.handleSystemInterruption(error) }
                })
                attach(stream, source: .system, configuration: settings.latencyProfile.chunkerConfiguration)
                warningMessage = nil
            } catch {
                warningMessage = error.localizedDescription
            }
        }
    }
}
