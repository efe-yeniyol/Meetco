import Foundation

/// Encodes mono Float32 samples as a 16-bit PCM WAV file in memory.
enum WAVEncoder {
    static func encode(samples: [Float], sampleRate: Int, normalize: Bool = true) -> Data {
        var gain: Float = 1
        if normalize {
            let peak = samples.reduce(Float(0)) { max($0, abs($1)) }
            // Gently boost quiet audio (e.g. low system volume); never boost more than 4x.
            if peak > 0.001 && peak < 0.5 { gain = min(4, 0.8 / peak) }
        }

        let dataSize = samples.count * 2
        var data = Data(capacity: 44 + dataSize)

        func append<T: FixedWidthInteger>(_ value: T) {
            var little = value.littleEndian
            withUnsafeBytes(of: &little) { data.append(contentsOf: $0) }
        }

        data.append(contentsOf: Array("RIFF".utf8))
        append(UInt32(36 + dataSize))
        data.append(contentsOf: Array("WAVE".utf8))
        data.append(contentsOf: Array("fmt ".utf8))
        append(UInt32(16))              // PCM chunk size
        append(UInt16(1))               // PCM format
        append(UInt16(1))               // mono
        append(UInt32(sampleRate))
        append(UInt32(sampleRate * 2))  // byte rate
        append(UInt16(2))               // block align
        append(UInt16(16))              // bits per sample
        data.append(contentsOf: Array("data".utf8))
        append(UInt32(dataSize))

        var pcm = [Int16](repeating: 0, count: samples.count)
        for index in samples.indices {
            let clipped = max(-1, min(1, samples[index] * gain))
            pcm[index] = Int16(clipped * Float(Int16.max)).littleEndian
        }
        pcm.withUnsafeBytes { data.append(contentsOf: $0) }
        return data
    }
}
