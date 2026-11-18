using Meetco.Core.Audio;
using Meetco.Core.Models;
using Meetco.Core.Transcription;
using static Meetco.Core.Tests.Signals;

namespace Meetco.Core.Tests;

public class AudioChunkerTests
{
    private static AudioChunker Chunker(AudioSourceKind source = AudioSourceKind.System, AudioChunkerConfiguration? configuration = null) =>
        new(source, configuration ?? new AudioChunkerConfiguration());

    [Fact]
    public void SilenceProducesNoChunks()
    {
        var chunker = Chunker();
        Assert.Empty(chunker.Append(Silence(5), DateTimeOffset.Now));
        Assert.Null(chunker.Flush(DateTimeOffset.Now));
    }

    [Fact]
    public void SpeechFollowedByPauseEmitsOneChunk()
    {
        var chunker = Chunker(AudioSourceKind.Microphone);
        Assert.Empty(chunker.Append(Tone(2), DateTimeOffset.Now));
        var chunks = chunker.Append(Silence(1.2), DateTimeOffset.Now);
        var chunk = Assert.Single(chunks);
        Assert.Equal(AudioSourceKind.Microphone, chunk.Source);
        Assert.True(chunk.Duration > 2.0);
        Assert.True(chunk.Duration < 2.9, "Trailing silence should be trimmed");
    }

    [Fact]
    public void ShortClickIsDiscarded()
    {
        var chunker = Chunker();
        chunker.Append(Tone(0.1), DateTimeOffset.Now);
        Assert.Empty(chunker.Append(Silence(2), DateTimeOffset.Now));
    }

    [Fact]
    public void LongSpeechIsSplitAtMaximumDuration()
    {
        var chunker = Chunker(configuration: new AudioChunkerConfiguration { SilenceToFlush = 0.8, MinimumChunkDuration = 1, MaximumChunkDuration = 5 });
        var chunks = chunker.Append(Tone(12), DateTimeOffset.Now);
        Assert.Equal(2, chunks.Count);
        Assert.InRange(chunks[0].Duration, 4.95, 5.05);
        Assert.True(chunker.HasActiveSpeech);
        Assert.NotNull(chunker.Flush(DateTimeOffset.Now));
    }

    [Fact]
    public void FlushEmitsPendingSpeech()
    {
        var chunker = Chunker();
        chunker.Append(Tone(1.5), DateTimeOffset.Now);
        Assert.NotNull(chunker.Flush(DateTimeOffset.Now));
        Assert.Null(chunker.Flush(DateTimeOffset.Now));
    }

    [Fact]
    public void ChunkerStreamsUtteranceThenCommits()
    {
        var chunker = Chunker();
        chunker.ForwardsLiveAudio = true;
        Assert.Empty(chunker.Append(Silence(1), DateTimeOffset.Now));
        Assert.Empty(chunker.DrainLiveEvents());

        chunker.Append(Tone(1), DateTimeOffset.Now);
        var early = Assert.IsType<LiveAudioEvent.Audio>(chunker.DrainLiveEvents().First());
        Assert.True(early.Samples.Length > SampleRate / 2, "Speech should stream before the utterance ends");

        var chunk = Assert.Single(chunker.Append(Silence(1.2), DateTimeOffset.Now));
        Assert.Equal(new LiveAudioEvent.Commit(chunk.Id), chunker.DrainLiveEvents().Last());
    }

    [Fact]
    public void ChunkerDiscardsNoise()
    {
        var chunker = Chunker();
        chunker.ForwardsLiveAudio = true;
        chunker.Append(Concat(Tone(0.1), Silence(1.5)), DateTimeOffset.Now);
        Assert.IsType<LiveAudioEvent.Discard>(chunker.DrainLiveEvents().Last());
    }

    [Fact]
    public void ProcessorFlushesWhenAudioStopsArriving()
    {
        var processor = new ChunkingProcessor(AudioSourceKind.System, new AudioChunkerConfiguration());
        var start = DateTimeOffset.Now;
        processor.Ingest(Tone(1), start);
        Assert.Null(processor.FlushIfIdle(start.AddSeconds(0.1)));
        Assert.NotNull(processor.FlushIfIdle(start.AddSeconds(1)));
    }

    [Fact]
    public void MutedProcessorIgnoresAudio()
    {
        var processor = new ChunkingProcessor(AudioSourceKind.Microphone, new AudioChunkerConfiguration());
        processor.SetMuted(true);
        var output = processor.Ingest(Concat(Tone(2), Silence(1.2)), DateTimeOffset.Now);
        Assert.Empty(output.Chunks);
        Assert.Equal(0, output.Level);
    }

    [Fact]
    public void UpsamplerProducesThreeSamplesForEveryTwo()
    {
        var upsampler = new LinearUpsampler();
        var total = 0;
        for (var i = 0; i < 10; i++) total += upsampler.Process(Tone(0.0205)).Length;
        var input = 10 * (int)(SampleRate * 0.0205);
        Assert.True(Math.Abs(total - input * 3 / 2) <= 2);
        Assert.Equal(new float[] { 0, 2, 4, 6, 8 }, new LinearUpsampler().Process(new float[] { 0, 3, 6, 9 }));
    }
}
