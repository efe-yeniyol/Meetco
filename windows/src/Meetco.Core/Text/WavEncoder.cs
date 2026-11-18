using System.Buffers.Binary;
using System.Text;

namespace Meetco.Core.Text;

/// <summary>Encodes mono float32 samples as a 16-bit PCM WAV file in memory.</summary>
public static class WavEncoder
{
    public static byte[] Encode(ReadOnlySpan<float> samples, int sampleRate, bool normalize = true)
    {
        var gain = 1f;
        if (normalize)
        {
            var peak = 0f;
            foreach (var sample in samples) peak = Math.Max(peak, Math.Abs(sample));
            // Gently boost quiet audio (e.g. low system volume); never boost more than 4x.
            if (peak > 0.001f && peak < 0.5f) gain = Math.Min(4f, 0.8f / peak);
        }

        var dataSize = samples.Length * 2;
        var data = new byte[44 + dataSize];
        var span = data.AsSpan();
        Encoding.ASCII.GetBytes("RIFF", span[0..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], (uint)(36 + dataSize));
        Encoding.ASCII.GetBytes("WAVE", span[8..12]);
        Encoding.ASCII.GetBytes("fmt ", span[12..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..20], 16);                   // PCM chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..22], 1);                    // PCM format
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..24], 1);                    // mono
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..28], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..32], (uint)(sampleRate * 2)); // byte rate
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..34], 2);                    // block align
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..36], 16);                   // bits per sample
        Encoding.ASCII.GetBytes("data", span[36..40]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..44], (uint)dataSize);

        for (var index = 0; index < samples.Length; index++)
        {
            var clipped = Math.Clamp(samples[index] * gain, -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(44 + index * 2, 2), (short)(clipped * short.MaxValue));
        }
        return data;
    }
}
