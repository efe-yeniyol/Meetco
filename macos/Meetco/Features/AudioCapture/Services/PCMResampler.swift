@preconcurrency import AVFoundation

/// Converts arbitrary PCM buffers into 16 kHz mono Float32 samples.
/// Each instance must only be used from one thread/queue at a time.
final class PCMResampler: @unchecked Sendable {
    static let targetSampleRate: Double = 16_000

    private let outputFormat = AVAudioFormat(
        commonFormat: .pcmFormatFloat32,
        sampleRate: PCMResampler.targetSampleRate,
        channels: 1,
        interleaved: false
    )!
    private var converter: AVAudioConverter?
    private var inputFormat: AVAudioFormat?

    func convert(_ buffer: AVAudioPCMBuffer) -> [Float] {
        guard buffer.frameLength > 0 else { return [] }

        if inputFormat == nil || inputFormat! != buffer.format {
            converter = AVAudioConverter(from: buffer.format, to: outputFormat)
            converter?.downmix = true
            converter?.sampleRateConverterQuality = AVAudioQuality.medium.rawValue
            inputFormat = buffer.format
        }
        guard let converter else { return [] }

        let ratio = outputFormat.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 64
        guard let output = AVAudioPCMBuffer(pcmFormat: outputFormat, frameCapacity: capacity) else { return [] }

        let feed = InputFeed()
        var error: NSError?
        let status = converter.convert(to: output, error: &error) { _, inputStatus in
            if feed.consumed {
                inputStatus.pointee = .noDataNow
                return nil
            }
            feed.consumed = true
            inputStatus.pointee = .haveData
            return buffer
        }
        guard status != .error, error == nil, let channel = output.floatChannelData?[0] else { return [] }
        return Array(UnsafeBufferPointer(start: channel, count: Int(output.frameLength)))
    }
}

/// Tracks whether the single input buffer was handed to the converter (called synchronously).
/// Tracks whether an AVAudioConverter input block already handed over its buffer.
final class InputFeed: @unchecked Sendable {
    var consumed = false
}
