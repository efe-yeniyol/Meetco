using System.Buffers.Binary;

namespace MeetcoApp.Platform;

/// <summary>Sample layouts delivered by WASAPI in shared mode.</summary>
public enum SampleEncoding { Float32, Pcm16, Pcm24, Pcm32 }

/// <summary>
/// Converts interleaved capture buffers (any channel count, any sample rate) to 16 kHz mono
/// float32: downmix by averaging channels, then a streaming windowed-sinc resampler whose
/// low-pass cutoff sits below the new Nyquist frequency so speech does not alias.
/// Not thread-safe: one instance per capture, called from the capture thread only.
/// </summary>
public sealed class AudioFormatConverter
{
    public const int TargetSampleRate = 16_000;

    /// <summary>Zero crossings on each side of the filter, measured in output samples.</summary>
    private const int ZeroCrossings = 16;
    /// <summary>Kernel table resolution: entries per input sample of distance.</summary>
    private const int TableResolution = 512;

    private readonly SampleEncoding _encoding;
    private readonly int _channels;
    private readonly int _bytesPerSample;
    private readonly bool _passThrough;
    private readonly double _step;
    private readonly double _cutoff;
    /// <summary>
    /// Half the filter length in input samples. It scales with the decimation ratio so the
    /// transition band stays narrow at the output rate (48 at 48 kHz, 96 taps in total);
    /// a fixed short filter would dull sibilants and let 8–11 kHz alias into the speech band.
    /// </summary>
    private readonly int _halfTaps;
    /// <summary>Precomputed kernel, sampled every 1/<see cref="TableResolution"/> input samples from 0 to <see cref="_halfTaps"/>.</summary>
    private readonly float[] _table;

    private readonly List<float> _history = [];
    /// <summary>Position of the next output sample, in input samples relative to the start of <see cref="_history"/>.</summary>
    private double _position;
    /// <summary>Bytes of an incomplete frame left over from the previous buffer.</summary>
    private byte[] _remainder = [];

    public AudioFormatConverter(SampleEncoding encoding, int channels, int sampleRate)
    {
        _encoding = encoding;
        _channels = Math.Max(1, channels);
        _bytesPerSample = encoding switch
        {
            SampleEncoding.Pcm16 => 2,
            SampleEncoding.Pcm24 => 3,
            _ => 4,
        };
        _passThrough = sampleRate == TargetSampleRate;
        _step = (double)sampleRate / TargetSampleRate;
        // Cycles per input sample: half the output rate, with a little margin for the filter's roll-off.
        _cutoff = 0.5 * Math.Min(1.0, (double)TargetSampleRate / sampleRate) * 0.92;
        _halfTaps = (int)Math.Ceiling(ZeroCrossings * Math.Max(1.0, _step));
        _table = new float[_halfTaps * TableResolution + 2];
        for (var index = 0; index < _table.Length; index++)
        {
            _table[index] = (float)ExactKernel((double)index / TableResolution);
        }
    }

    public float[] Convert(byte[] buffer, int count)
    {
        var mono = Downmix(buffer, count);
        return _passThrough ? mono : Resample(mono);
    }

    private float[] Downmix(byte[] buffer, int count)
    {
        ReadOnlySpan<byte> data = buffer.AsSpan(0, count);
        byte[]? joined = null;
        if (_remainder.Length > 0)
        {
            joined = new byte[_remainder.Length + count];
            _remainder.CopyTo(joined, 0);
            buffer.AsSpan(0, count).CopyTo(joined.AsSpan(_remainder.Length));
            data = joined;
        }

        var frameBytes = _bytesPerSample * _channels;
        var frames = data.Length / frameBytes;
        var leftover = data.Length - frames * frameBytes;
        _remainder = leftover > 0 ? data[^leftover..].ToArray() : [];

        var output = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            var offset = frame * frameBytes;
            for (var channel = 0; channel < _channels; channel++)
            {
                sum += ReadSample(data.Slice(offset + channel * _bytesPerSample, _bytesPerSample));
            }
            output[frame] = sum / _channels;
        }
        return output;
    }

    private float ReadSample(ReadOnlySpan<byte> bytes)
    {
        switch (_encoding)
        {
            case SampleEncoding.Float32:
                var value = BinaryPrimitives.ReadSingleLittleEndian(bytes);
                return float.IsFinite(value) ? value : 0f;
            case SampleEncoding.Pcm16:
                return BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f;
            case SampleEncoding.Pcm24:
                var raw = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
                if ((raw & 0x800000) != 0) raw |= unchecked((int)0xFF000000);
                return raw / 8388608f;
            default:
                return BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f;
        }
    }

    private float[] Resample(float[] input)
    {
        _history.AddRange(input);
        var output = new List<float>(input.Length * TargetSampleRate / 16_000 + 4);
        while (true)
        {
            var center = (int)Math.Floor(_position);
            if (center + _halfTaps >= _history.Count) break;
            double sum = 0;
            for (var k = center - _halfTaps + 1; k <= center + _halfTaps; k++)
            {
                if (k < 0) continue;
                sum += _history[k] * Kernel(_position - k);
            }
            output.Add((float)sum);
            _position += _step;
        }

        // Drop history that no future output sample can reach.
        var drop = (int)Math.Floor(_position) - _halfTaps;
        if (drop > 0)
        {
            drop = Math.Min(drop, _history.Count);
            _history.RemoveRange(0, drop);
            _position -= drop;
        }
        return output.ToArray();
    }

    /// <summary>Table lookup with linear interpolation (the kernel is symmetric).</summary>
    private double Kernel(double distance)
    {
        var scaled = Math.Abs(distance) * TableResolution;
        var index = (int)scaled;
        if (index >= _table.Length - 1) return 0;
        var fraction = scaled - index;
        return _table[index] + (_table[index + 1] - _table[index]) * fraction;
    }

    /// <summary>Blackman-windowed sinc low-pass, normalized for unity gain at DC.</summary>
    private double ExactKernel(double distance)
    {
        var u = distance / _halfTaps;
        if (Math.Abs(u) >= 1) return 0;
        var window = 0.42 + 0.5 * Math.Cos(Math.PI * u) + 0.08 * Math.Cos(2 * Math.PI * u);
        var x = 2 * Math.PI * _cutoff * distance;
        var sinc = Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(x) / x;
        return 2 * _cutoff * sinc * window;
    }
}
