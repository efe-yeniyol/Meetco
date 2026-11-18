using Meetco.Core.Models;

namespace Meetco.Core.Audio;

/// <summary>
/// Energy-based voice activity detector that groups audio into utterance-sized chunks.
/// Silence is never sent to the API, short noises are discarded, and chunks end at natural
/// pauses so transcription stays responsive without paying for tiny requests.
/// Not thread-safe: see <see cref="ChunkingProcessor"/>.
/// </summary>
public sealed class AudioChunker
{
    private readonly List<float> _pendingFrame = [];
    private readonly List<float> _preRoll = [];
    private readonly List<float> _current = [];
    private DateTimeOffset? _currentStart;
    private bool _isInChunk;
    private int _speechSamples;
    private int _trailingSilenceSamples;
    private float _noiseFloor = 0.002f;
    private int _sequence;
    private readonly List<LiveAudioEvent> _liveEvents = [];

    public AudioChunker(AudioSourceKind source, AudioChunkerConfiguration configuration)
    {
        Source = source;
        Configuration = configuration;
    }

    public AudioSourceKind Source { get; }
    public AudioChunkerConfiguration Configuration { get; }

    /// <summary>When true, the chunker also reports utterance audio as it happens (see <see cref="DrainLiveEvents"/>).</summary>
    public bool ForwardsLiveAudio { get; set; }

    /// <summary>RMS of the most recent analysis frame (0…1), for level meters.</summary>
    public float LastLevel { get; private set; }

    public bool HasActiveSpeech => _isInChunk;

    private double SampleRate => Configuration.SampleRate;
    private int FrameLength => Math.Max(1, (int)(SampleRate * Configuration.FrameDuration));

    /// <summary>Feeds new samples captured at <paramref name="time"/> (arrival time of the last sample).</summary>
    public List<AudioChunk> Append(ReadOnlySpan<float> samples, DateTimeOffset time)
    {
        var emitted = new List<AudioChunk>();
        foreach (var sample in samples) _pendingFrame.Add(sample);
        var frameLength = FrameLength;
        var offset = 0;
        var total = _pendingFrame.Count;
        while (total - offset >= frameLength)
        {
            var frame = _pendingFrame.GetRange(offset, frameLength).ToArray();
            offset += frameLength;
            var remaining = (total - offset) / SampleRate;
            var chunk = Process(frame, time.AddSeconds(-remaining));
            if (chunk is not null) emitted.Add(chunk);
        }
        _pendingFrame.RemoveRange(0, offset);
        return emitted;
    }

    /// <summary>Ends the current utterance (e.g. stream went quiet or session stopped).</summary>
    public AudioChunk? Flush(DateTimeOffset time) => _isInChunk ? Emit(time) : null;

    /// <summary>
    /// Ordered events for a live transcription stream since the last call: the audio of the
    /// utterance in progress, then Commit when it ends (or Discard when it was only noise).
    /// </summary>
    public List<LiveAudioEvent> DrainLiveEvents()
    {
        var events = new List<LiveAudioEvent>(_liveEvents);
        _liveEvents.Clear();
        return events;
    }

    public void Reset()
    {
        if (_isInChunk) DiscardLive();
        _pendingFrame.Clear();
        _preRoll.Clear();
        ResetChunk();
        LastLevel = 0;
    }

    public static float Rms(ReadOnlySpan<float> frame)
    {
        if (frame.IsEmpty) return 0;
        float sum = 0;
        foreach (var sample in frame) sum += sample * sample;
        return MathF.Sqrt(sum / frame.Length);
    }

    private void Forward(float[] samples)
    {
        if (!ForwardsLiveAudio || samples.Length == 0) return;
        if (_liveEvents.Count > 0 && _liveEvents[^1] is LiveAudioEvent.Audio pending)
        {
            var merged = new float[pending.Samples.Length + samples.Length];
            pending.Samples.CopyTo(merged, 0);
            samples.CopyTo(merged, pending.Samples.Length);
            _liveEvents[^1] = new LiveAudioEvent.Audio(merged);
        }
        else
        {
            _liveEvents.Add(new LiveAudioEvent.Audio(samples));
        }
    }

    private void DiscardLive()
    {
        if (ForwardsLiveAudio) _liveEvents.Add(new LiveAudioEvent.Discard());
    }

    private AudioChunk? Process(float[] frame, DateTimeOffset frameEnd)
    {
        var rms = Rms(frame);
        LastLevel = Math.Min(1, rms * 8);
        var threshold = Math.Max(Configuration.MinimumSpeechRms, _noiseFloor * Configuration.NoiseFloorMultiplier);
        var isSpeech = rms > threshold;

        if (!isSpeech)
        {
            // Adapt to the background noise level only during non-speech.
            _noiseFloor = _noiseFloor * 0.97f + rms * 0.03f;
        }

        if (!_isInChunk)
        {
            if (!isSpeech)
            {
                _preRoll.AddRange(frame);
                var maxPreRoll = (int)(SampleRate * Configuration.PreRollDuration);
                if (_preRoll.Count > maxPreRoll) _preRoll.RemoveRange(0, _preRoll.Count - maxPreRoll);
                return null;
            }
            _isInChunk = true;
            _current.Clear();
            _current.AddRange(_preRoll);
            _current.AddRange(frame);
            Forward(_current.ToArray());
            var startOffset = _current.Count / SampleRate;
            _currentStart = frameEnd.AddSeconds(-startOffset);
            _preRoll.Clear();
            _speechSamples = frame.Length;
            _trailingSilenceSamples = 0;
            return null;
        }

        _current.AddRange(frame);
        Forward(frame);
        if (isSpeech)
        {
            _speechSamples += frame.Length;
            _trailingSilenceSamples = 0;
        }
        else
        {
            _trailingSilenceSamples += frame.Length;
        }

        var duration = _current.Count / SampleRate;
        var silence = _trailingSilenceSamples / SampleRate;
        var speech = _speechSamples / SampleRate;

        if (duration >= Configuration.MaximumChunkDuration) return Emit(frameEnd);
        if (silence >= Configuration.SilenceToFlush)
        {
            if (speech < Configuration.MinimumSpeechDuration)
            {
                // A click or short noise: drop it.
                DiscardLive();
                ResetChunk();
                return null;
            }
            if (duration >= Configuration.MinimumChunkDuration) return Emit(frameEnd);
        }
        return null;
    }

    private AudioChunk? Emit(DateTimeOffset time)
    {
        try
        {
            var speech = _speechSamples / SampleRate;
            if (speech < Configuration.MinimumSpeechDuration || _currentStart is not { } start)
            {
                DiscardLive();
                return null;
            }

            var samples = _current.ToArray();
            var keep = (int)(SampleRate * Configuration.TrailingSilenceKept);
            var trim = _trailingSilenceSamples - keep;
            if (trim > 0 && trim < samples.Length) samples = samples[..^trim];

            _sequence += 1;
            var end = start.AddSeconds(samples.Length / SampleRate);
            var latest = time > start ? time : start;
            var chunk = new AudioChunk
            {
                Source = Source,
                Samples = samples,
                SampleRate = Configuration.SampleRate,
                StartedAt = start,
                EndedAt = end < latest ? end : latest,
                Sequence = _sequence,
            };
            if (ForwardsLiveAudio) _liveEvents.Add(new LiveAudioEvent.Commit(chunk.Id));
            return chunk;
        }
        finally
        {
            ResetChunk();
        }
    }

    private void ResetChunk()
    {
        _current.Clear();
        _currentStart = null;
        _isInChunk = false;
        _speechSamples = 0;
        _trailingSilenceSamples = 0;
    }
}
