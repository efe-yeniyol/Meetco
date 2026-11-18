using Meetco.Core.Models;

namespace Meetco.Core.Audio;

/// <summary>
/// Owns one source's chunker behind a lock so audio callbacks and the idle timer never race.
/// </summary>
public sealed class ChunkingProcessor
{
    public readonly record struct Output(IReadOnlyList<AudioChunk> Chunks, float Level);

    private readonly object _gate = new();
    private readonly AudioChunker _chunker;
    /// <summary>Live transcription stream that hears the utterance while it is still being spoken.</summary>
    private readonly ILiveAudioSink? _liveSink;
    private bool _isMuted;
    private DateTimeOffset? _lastInputAt;

    public ChunkingProcessor(AudioSourceKind source, AudioChunkerConfiguration configuration, ILiveAudioSink? liveSink = null)
    {
        Source = source;
        _chunker = new AudioChunker(source, configuration) { ForwardsLiveAudio = liveSink is not null };
        _liveSink = liveSink;
    }

    public AudioSourceKind Source { get; }

    public Output Ingest(ReadOnlySpan<float> samples, DateTimeOffset time)
    {
        lock (_gate)
        {
            _lastInputAt = time;
            if (_isMuted) return new Output([], 0);
            var chunks = _chunker.Append(samples, time);
            ForwardLiveEvents();
            return new Output(chunks, _chunker.LastLevel);
        }
    }

    /// <summary>
    /// Flushes an utterance when the stream has delivered no audio for a while
    /// (loopback capture stops delivering buffers during total silence).
    /// </summary>
    public AudioChunk? FlushIfIdle(DateTimeOffset now, double idleThreshold = 0.45)
    {
        lock (_gate)
        {
            if (!_chunker.HasActiveSpeech || _lastInputAt is not { } last || (now - last).TotalSeconds < idleThreshold) return null;
            var chunk = _chunker.Flush(now);
            ForwardLiveEvents();
            return chunk;
        }
    }

    public AudioChunk? Finish(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_isMuted) return null;
            var chunk = _chunker.Flush(now);
            ForwardLiveEvents();
            return chunk;
        }
    }

    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            _isMuted = muted;
            if (!muted) return;
            _chunker.Reset();
            ForwardLiveEvents();
        }
    }

    /// <summary>
    /// Forwards pending live events in order. Called before chunks are handed out, so the
    /// stream always knows about a commit before anyone asks for that chunk's transcript.
    /// </summary>
    private void ForwardLiveEvents()
    {
        if (_liveSink is null) return;
        foreach (var liveEvent in _chunker.DrainLiveEvents()) _liveSink.Send(liveEvent);
    }
}
