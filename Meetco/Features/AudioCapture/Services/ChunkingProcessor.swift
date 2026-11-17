import Foundation

/// Actor that owns one source's chunker so audio callbacks and the idle timer never race.
actor ChunkingProcessor {
    struct Output: Sendable {
        var chunks: [AudioChunk]
        var level: Float
    }

    let source: AudioSourceKind
    private var chunker: AudioChunker
    private var isMuted = false
    private var lastInputAt: Date?
    /// Live transcription stream that hears the utterance while it is still being spoken.
    private let liveSink: (any LiveAudioSink)?

    init(source: AudioSourceKind, configuration: AudioChunkerConfiguration, liveSink: (any LiveAudioSink)? = nil) {
        self.source = source
        self.chunker = AudioChunker(source: source, configuration: configuration)
        self.liveSink = liveSink
        chunker.forwardsLiveAudio = liveSink != nil
    }

    /// Forwards pending live events in order. Called before chunks are handed out, so the
    /// stream always knows about a commit before anyone asks for that chunk's transcript.
    private func forwardLiveEvents() {
        guard let liveSink else { return }
        for event in chunker.drainLiveEvents() { liveSink.send(event) }
    }

    func ingest(_ samples: [Float], at time: Date) -> Output {
        lastInputAt = time
        guard !isMuted else { return Output(chunks: [], level: 0) }
        let chunks = chunker.append(samples, at: time)
        forwardLiveEvents()
        return Output(chunks: chunks, level: chunker.lastLevel)
    }

    /// Flushes an utterance when the stream has delivered no audio for a while
    /// (ScreenCaptureKit may stop delivering buffers during total silence).
    func flushIfIdle(now: Date, idleThreshold: TimeInterval = 0.45) -> AudioChunk? {
        guard chunker.hasActiveSpeech, let lastInputAt, now.timeIntervalSince(lastInputAt) >= idleThreshold else {
            return nil
        }
        defer { forwardLiveEvents() }
        return chunker.flush(at: now)
    }

    func finish(now: Date) -> AudioChunk? {
        guard !isMuted else { return nil }
        defer { forwardLiveEvents() }
        return chunker.flush(at: now)
    }

    func setMuted(_ muted: Bool) {
        isMuted = muted
        if muted {
            chunker.reset()
            forwardLiveEvents()
        }
    }
}
