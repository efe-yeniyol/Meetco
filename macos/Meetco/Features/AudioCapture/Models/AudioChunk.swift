import Foundation

/// A speech segment cut from a continuous audio stream, ready for transcription.
struct AudioChunk: Sendable, Identifiable {
    let id = UUID()
    let source: AudioSourceKind
    /// 16 kHz mono Float32 samples.
    let samples: [Float]
    let sampleRate: Int
    let startedAt: Date
    let endedAt: Date
    let sequence: Int

    var duration: TimeInterval { Double(samples.count) / Double(sampleRate) }
}

/// What a live (streaming) transcription connection needs to know about an utterance, in order.
enum LiveAudioEvent: Sendable, Equatable {
    /// More audio of the utterance in progress (16 kHz mono Float32).
    case audio([Float])
    /// The utterance ended and became the chunk with this id.
    case commit(UUID)
    /// The audio sent since the last commit was noise and must be ignored.
    case discard
}

/// Receives utterance audio while it is being spoken. Must be cheap and non-blocking.
protocol LiveAudioSink: Sendable {
    func send(_ event: LiveAudioEvent)
}

/// Tuning for voice-activity based chunking.
struct AudioChunkerConfiguration: Sendable, Equatable {
    var sampleRate: Int = 16_000
    /// Analysis frame length in seconds.
    var frameDuration: Double = 0.02
    /// Trailing silence that ends an utterance.
    var silenceToFlush: Double = 0.8
    /// Shortest chunk that is flushed at a pause.
    var minimumChunkDuration: Double = 1.2
    /// Hard upper bound; long monologues are split at this length.
    var maximumChunkDuration: Double = 12
    /// Minimum voiced audio required before a chunk is worth sending.
    var minimumSpeechDuration: Double = 0.35
    /// Audio kept before speech onset so first syllables are not cut.
    var preRollDuration: Double = 0.3
    /// Trailing silence kept at the end of a chunk.
    var trailingSilenceKept: Double = 0.25
    /// Absolute RMS floor for speech detection (~ -44 dBFS).
    var minimumSpeechRMS: Float = 0.006
    /// Speech must be this many times louder than the adaptive noise floor.
    var noiseFloorMultiplier: Float = 3

    init(
        silenceToFlush: Double = 0.8,
        minimumChunkDuration: Double = 1.2,
        maximumChunkDuration: Double = 12
    ) {
        self.silenceToFlush = silenceToFlush
        self.minimumChunkDuration = minimumChunkDuration
        self.maximumChunkDuration = maximumChunkDuration
    }
}
