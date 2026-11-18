using Meetco.Core.Models;

namespace Meetco.Core.Audio;

/// <summary>A speech segment cut from a continuous audio stream, ready for transcription.</summary>
public sealed class AudioChunk
{
    public Guid Id { get; } = Guid.NewGuid();
    public required AudioSourceKind Source { get; init; }
    /// <summary>16 kHz mono float32 samples.</summary>
    public required float[] Samples { get; init; }
    public required int SampleRate { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required int Sequence { get; init; }

    public double Duration => (double)Samples.Length / SampleRate;
}

/// <summary>What a live (streaming) transcription connection needs to know about an utterance, in order.</summary>
public abstract record LiveAudioEvent
{
    private LiveAudioEvent() { }

    /// <summary>More audio of the utterance in progress (16 kHz mono float32).</summary>
    public sealed record Audio(float[] Samples) : LiveAudioEvent;

    /// <summary>The utterance ended and became the chunk with this id.</summary>
    public sealed record Commit(Guid ChunkId) : LiveAudioEvent;

    /// <summary>The audio sent since the last commit was noise and must be ignored.</summary>
    public sealed record Discard : LiveAudioEvent;
}

/// <summary>Receives utterance audio while it is being spoken. Must be cheap and non-blocking.</summary>
public interface ILiveAudioSink
{
    void Send(LiveAudioEvent audioEvent);
}

/// <summary>Tuning for voice-activity based chunking.</summary>
public sealed record AudioChunkerConfiguration
{
    public int SampleRate { get; init; } = 16_000;
    /// <summary>Analysis frame length in seconds.</summary>
    public double FrameDuration { get; init; } = 0.02;
    /// <summary>Trailing silence that ends an utterance.</summary>
    public double SilenceToFlush { get; init; } = 0.8;
    /// <summary>Shortest chunk that is flushed at a pause.</summary>
    public double MinimumChunkDuration { get; init; } = 1.2;
    /// <summary>Hard upper bound; long monologues are split at this length.</summary>
    public double MaximumChunkDuration { get; init; } = 12;
    /// <summary>Minimum voiced audio required before a chunk is worth sending.</summary>
    public double MinimumSpeechDuration { get; init; } = 0.35;
    /// <summary>Audio kept before speech onset so first syllables are not cut.</summary>
    public double PreRollDuration { get; init; } = 0.3;
    /// <summary>Trailing silence kept at the end of a chunk.</summary>
    public double TrailingSilenceKept { get; init; } = 0.25;
    /// <summary>Absolute RMS floor for speech detection (~ -44 dBFS).</summary>
    public float MinimumSpeechRms { get; init; } = 0.006f;
    /// <summary>Speech must be this many times louder than the adaptive noise floor.</summary>
    public float NoiseFloorMultiplier { get; init; } = 3;
}
