import Foundation

/// Energy-based voice activity detector that groups audio into utterance-sized chunks.
///
/// Silence is never sent to the API, short noises are discarded, and chunks end at natural
/// pauses so transcription stays responsive without paying for tiny requests.
struct AudioChunker {
    let source: AudioSourceKind
    let configuration: AudioChunkerConfiguration

    private var pendingFrame: [Float] = []
    private var preRoll: [Float] = []
    private var current: [Float] = []
    private var currentStart: Date?
    private var isInChunk = false
    private var speechSamples = 0
    private var trailingSilenceSamples = 0
    private var noiseFloor: Float = 0.002
    private var sequence = 0

    /// When true, the chunker also reports utterance audio as it happens (see `drainLiveEvents`).
    var forwardsLiveAudio = false
    private var liveEvents: [LiveAudioEvent] = []

    /// RMS of the most recent analysis frame (0…1), for level meters.
    private(set) var lastLevel: Float = 0

    init(source: AudioSourceKind, configuration: AudioChunkerConfiguration) {
        self.source = source
        self.configuration = configuration
    }

    private var sampleRate: Double { Double(configuration.sampleRate) }
    private var frameLength: Int { max(1, Int(sampleRate * configuration.frameDuration)) }
    var hasActiveSpeech: Bool { isInChunk }

    /// Feeds new samples captured at `time` (arrival time of the last sample).
    mutating func append(_ samples: [Float], at time: Date) -> [AudioChunk] {
        var emitted: [AudioChunk] = []
        pendingFrame.append(contentsOf: samples)
        var offset = 0
        let total = pendingFrame.count
        while total - offset >= frameLength {
            let frame = Array(pendingFrame[offset..<(offset + frameLength)])
            offset += frameLength
            let remaining = Double(total - offset) / sampleRate
            if let chunk = process(frame: frame, frameEnd: time.addingTimeInterval(-remaining)) {
                emitted.append(chunk)
            }
        }
        pendingFrame.removeFirst(offset)
        return emitted
    }

    /// Ends the current utterance (e.g. stream went quiet or session stopped).
    mutating func flush(at time: Date) -> AudioChunk? {
        guard isInChunk else { return nil }
        return emit(at: time)
    }

    /// Ordered events for a live transcription stream since the last call: the audio of the
    /// utterance in progress, then `commit` when it ends (or `discard` when it was only noise).
    mutating func drainLiveEvents() -> [LiveAudioEvent] {
        defer { liveEvents.removeAll(keepingCapacity: true) }
        return liveEvents
    }

    private mutating func forward(_ samples: [Float]) {
        guard forwardsLiveAudio, !samples.isEmpty else { return }
        if case .audio(let pending)? = liveEvents.last {
            liveEvents[liveEvents.count - 1] = .audio(pending + samples)
        } else {
            liveEvents.append(.audio(samples))
        }
    }

    private mutating func discardLive() {
        guard forwardsLiveAudio else { return }
        liveEvents.append(.discard)
    }

    mutating func reset() {
        if isInChunk { discardLive() }
        pendingFrame.removeAll()
        preRoll.removeAll()
        resetChunk()
        lastLevel = 0
    }

    // MARK: Private

    private mutating func process(frame: [Float], frameEnd: Date) -> AudioChunk? {
        let rms = Self.rms(frame)
        lastLevel = min(1, rms * 8)
        let threshold = max(configuration.minimumSpeechRMS, noiseFloor * configuration.noiseFloorMultiplier)
        let isSpeech = rms > threshold

        if !isSpeech {
            // Adapt to the background noise level only during non-speech.
            noiseFloor = noiseFloor * 0.97 + rms * 0.03
        }

        if !isInChunk {
            guard isSpeech else {
                preRoll.append(contentsOf: frame)
                let maxPreRoll = Int(sampleRate * configuration.preRollDuration)
                if preRoll.count > maxPreRoll { preRoll.removeFirst(preRoll.count - maxPreRoll) }
                return nil
            }
            isInChunk = true
            current = preRoll + frame
            forward(current)
            let startOffset = Double(current.count) / sampleRate
            currentStart = frameEnd.addingTimeInterval(-startOffset)
            preRoll.removeAll()
            speechSamples = frame.count
            trailingSilenceSamples = 0
            return nil
        }

        current.append(contentsOf: frame)
        forward(frame)
        if isSpeech {
            speechSamples += frame.count
            trailingSilenceSamples = 0
        } else {
            trailingSilenceSamples += frame.count
        }

        let duration = Double(current.count) / sampleRate
        let silence = Double(trailingSilenceSamples) / sampleRate
        let speech = Double(speechSamples) / sampleRate

        if duration >= configuration.maximumChunkDuration {
            return emit(at: frameEnd)
        }
        if silence >= configuration.silenceToFlush {
            if speech < configuration.minimumSpeechDuration {
                // A click or short noise — drop it.
                discardLive()
                resetChunk()
                return nil
            }
            if duration >= configuration.minimumChunkDuration {
                return emit(at: frameEnd)
            }
        }
        return nil
    }

    private mutating func emit(at time: Date) -> AudioChunk? {
        defer { resetChunk() }
        let speech = Double(speechSamples) / sampleRate
        guard speech >= configuration.minimumSpeechDuration, let start = currentStart else {
            discardLive()
            return nil
        }

        var samples = current
        let keep = Int(sampleRate * configuration.trailingSilenceKept)
        let trim = trailingSilenceSamples - keep
        if trim > 0 && trim < samples.count { samples.removeLast(trim) }

        sequence += 1
        let end = start.addingTimeInterval(Double(samples.count) / sampleRate)
        let chunk = AudioChunk(
            source: source,
            samples: samples,
            sampleRate: configuration.sampleRate,
            startedAt: start,
            endedAt: min(end, max(time, start)),
            sequence: sequence
        )
        if forwardsLiveAudio { liveEvents.append(.commit(chunk.id)) }
        return chunk
    }

    private mutating func resetChunk() {
        current.removeAll(keepingCapacity: true)
        currentStart = nil
        isInChunk = false
        speechSamples = 0
        trailingSilenceSamples = 0
    }

    static func rms(_ frame: [Float]) -> Float {
        guard !frame.isEmpty else { return 0 }
        var sum: Float = 0
        for sample in frame { sum += sample * sample }
        return (sum / Float(frame.count)).squareRoot()
    }
}
