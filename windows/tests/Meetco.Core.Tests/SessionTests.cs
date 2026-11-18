using System.Text.Json.Nodes;
using Meetco.Core.Assistant;
using Meetco.Core.Audio;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;
using Meetco.Core.Session;
using Meetco.Core.Settings;
using Meetco.Core.Transcription;

namespace Meetco.Core.Tests;

public class SuggestionEngineTests
{
    private sealed class Harness
    {
        public TranscriptStore Store { get; } = new();
        public AppSettings Settings { get; } = new();
        public ScriptedAssistant Assistant { get; } = new();
        public SuggestionEngine Engine { get; }

        public Harness(string? key = "sk-test")
        {
            Engine = new SuggestionEngine(Assistant, null, Store, Settings, new ApiKeyRegistry(new ApiKeyProvider(new MemoryKeyStore(key)), _ => new MemoryKeyStore()));
        }

        public TranscriptSegment Segment(string text, AudioSourceKind source = AudioSourceKind.System, bool isQuestion = true)
        {
            var now = DateTimeOffset.Now;
            var segment = new TranscriptSegment { Source = source, Text = text, StartedAt = now.AddSeconds(-1), EndedAt = now, IsQuestion = isQuestion };
            Store.Insert(segment);
            return segment;
        }
    }

    [Fact]
    public void AnswersParticipantQuestionAfterDebounce() => SingleThread.Run(async () =>
    {
        var harness = new Harness();
        harness.Engine.HandleNewSegment(harness.Segment("How long does the integration take"), AudioCaptureMode.SystemAndMicrophone);
        Assert.Empty(harness.Assistant.Requests);
        await Task.Delay(400);
        var request = Assert.Single(harness.Assistant.Requests);
        Assert.Equal("How long does the integration take", request.FocusQuestion);
        var suggestion = Assert.Single(harness.Engine.Suggestions);
        Assert.Equal("Answer to How long does the integration take", suggestion.Text);
        Assert.True(suggestion.IsAutomatic);
        Assert.False(harness.Engine.IsGenerating);
    });

    [Fact]
    public void QuestionMarkAnswersImmediately() => SingleThread.Run(async () =>
    {
        var harness = new Harness();
        harness.Engine.HandleNewSegment(harness.Segment("Can you do it?"), AudioCaptureMode.SystemAndMicrophone);
        Assert.Single(harness.Assistant.Requests);
        await Task.Yield();
    });

    [Fact]
    public void IgnoresUserOwnQuestionsAndDuplicates() => SingleThread.Run(async () =>
    {
        var harness = new Harness();
        harness.Engine.HandleNewSegment(harness.Segment("Can you hear me?", AudioSourceKind.Microphone), AudioCaptureMode.SystemAndMicrophone);
        Assert.Empty(harness.Assistant.Requests);

        harness.Engine.HandleNewSegment(harness.Segment("What is the price?"), AudioCaptureMode.SystemAndMicrophone);
        harness.Engine.HandleNewSegment(harness.Segment("What is the price?"), AudioCaptureMode.SystemAndMicrophone);
        await Task.Delay(50);
        Assert.Single(harness.Assistant.Requests);
    });

    [Fact]
    public void MicrophoneOnlyModeAnswersEveryone() => SingleThread.Run(async () =>
    {
        var harness = new Harness();
        harness.Engine.HandleNewSegment(harness.Segment("Can you hear me?", AudioSourceKind.Microphone), AudioCaptureMode.MicrophoneOnly);
        await Task.Delay(50);
        Assert.Single(harness.Assistant.Requests);
    });

    [Fact]
    public void NothingHappensWithoutKeyOrWhenDisabled() => SingleThread.Run(async () =>
    {
        var harness = new Harness(key: null);
        harness.Engine.HandleNewSegment(harness.Segment("Can you do it?"), AudioCaptureMode.SystemAndMicrophone);
        var disabled = new Harness();
        disabled.Settings.AutoSuggest = false;
        disabled.Engine.HandleNewSegment(disabled.Segment("Can you do it?"), AudioCaptureMode.SystemAndMicrophone);
        await Task.Delay(50);
        Assert.Empty(harness.Assistant.Requests);
        Assert.Empty(disabled.Assistant.Requests);
    });

    [Fact]
    public void ModelSayingNoQuestionAddsNothing() => SingleThread.Run(async () =>
    {
        var harness = new Harness();
        harness.Assistant.Respond = _ => new AssistantOutput { QuestionDetected = false };
        harness.Engine.HandleNewSegment(harness.Segment("Can you believe this weather?"), AudioCaptureMode.SystemAndMicrophone);
        await Task.Delay(50);
        Assert.Empty(harness.Engine.Suggestions);
    });

    [Fact]
    public void ManualActionsAndHistory() => SingleThread.Run(async () =>
    {
        var harness = new Harness();
        harness.Assistant.Respond = request => new AssistantOutput { Text = request.Kind.ToString() };
        harness.Engine.Recap();
        harness.Engine.WhatShouldISay();
        await Task.Delay(50);
        Assert.Equal(2, harness.Engine.Suggestions.Count);
        Assert.Equal("WhatToSay", harness.Engine.CurrentSuggestion?.Text);
        Assert.Equal("1/2", harness.Engine.HistoryLabel);

        harness.Engine.ShowOlder();
        Assert.Equal("Recap", harness.Engine.CurrentSuggestion?.Text);
        Assert.False(harness.Engine.CanShowOlder);
        harness.Engine.ShowNewer();
        Assert.Equal("WhatToSay", harness.Engine.CurrentSuggestion?.Text);

        var newest = harness.Engine.Suggestions[0];
        harness.Engine.Regenerate(newest);
        await Task.Delay(50);
        Assert.Equal(2, harness.Engine.Suggestions.Count);
        Assert.Equal(newest.Id, harness.Engine.Suggestions[0].Id);
        Assert.Equal(newest.Text, harness.Assistant.Requests.Last().PreviousAnswers[0]);

        harness.Engine.Dismiss(newest);
        Assert.Single(harness.Engine.Suggestions);
        harness.Engine.Reset();
        Assert.Empty(harness.Engine.Suggestions);
        Assert.Null(harness.Engine.CurrentSuggestion);
    });
}

public class TranscriptionCoordinatorTests
{
    private sealed class FixedTranscriber(params string[] texts) : ITranscriptionService
    {
        private readonly Queue<string> _texts = new(texts);

        public Task<TranscriptionResult> TranscribeAsync(AudioChunk chunk, TranscriptionOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new TranscriptionResult(_texts.Dequeue(), null));
    }

    private static AudioChunk Chunk(AudioSourceKind source, double secondsAgo)
    {
        var start = DateTimeOffset.Now.AddSeconds(-secondsAgo);
        return new AudioChunk { Source = source, Samples = new float[16_000], SampleRate = 16_000, StartedAt = start, EndedAt = start.AddSeconds(1), Sequence = 1 };
    }

    [Fact]
    public void FiltersCleansDetectsAndOrders() => SingleThread.Run(async () =>
    {
        var store = new TranscriptStore();
        var coordinator = new TranscriptionCoordinator(
            new FixedTranscriber("Thank you for watching!", "[Music] Bu sistemi yarın kurabilir misiniz", "Bu sistemi yarın kurabilir misiniz", "Hello team."),
            store, new AppSettings());
        var added = new List<TranscriptSegment>();
        coordinator.SegmentAdded += added.Add;

        coordinator.Process(Chunk(AudioSourceKind.System, 10));
        coordinator.Process(Chunk(AudioSourceKind.System, 8));
        coordinator.Process(Chunk(AudioSourceKind.System, 6));
        coordinator.Process(Chunk(AudioSourceKind.Microphone, 20));
        await coordinator.WaitUntilIdleAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, store.Segments.Count);
        Assert.Equal(AudioSourceKind.Microphone, store.Segments[0].Source);
        var question = store.Segments[1];
        Assert.Equal("Bu sistemi yarın kurabilir misiniz", question.Text);
        Assert.True(question.IsQuestion);
        Assert.Equal("tr", question.LanguageCode);
        Assert.Equal(2, added.Count);
        Assert.False(coordinator.IsTranscribing);
    });
}

public class TranscriptionIdleTests
{
    private sealed class GatedTranscriber : ITranscriptionService
    {
        public List<TaskCompletionSource<TranscriptionResult>> Pending { get; } = [];

        public Task<TranscriptionResult> TranscribeAsync(AudioChunk chunk, TranscriptionOptions options, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<TranscriptionResult>();
            Pending.Add(completion);
            return completion.Task;
        }
    }

    [Fact]
    public void WaitUntilIdleIsNotFooledByAnEarlierTimeout() => SingleThread.Run(async () =>
    {
        var transcriber = new GatedTranscriber();
        var coordinator = new TranscriptionCoordinator(transcriber, new TranscriptStore(), new AppSettings());
        AudioChunk Chunk() => new() { Source = AudioSourceKind.System, Samples = new float[16], SampleRate = 16_000, StartedAt = DateTimeOffset.Now, EndedAt = DateTimeOffset.Now, Sequence = 1 };

        coordinator.Process(Chunk());
        await coordinator.WaitUntilIdleAsync(TimeSpan.FromMilliseconds(20));   // times out
        transcriber.Pending[0].SetResult(new TranscriptionResult("One.", "en"));
        await Task.Delay(20);
        Assert.False(coordinator.IsTranscribing);

        coordinator.Process(Chunk());
        var wait = coordinator.WaitUntilIdleAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.False(wait.IsCompleted, "Must wait for the new transcription, not reuse the old signal");
        transcriber.Pending[1].SetResult(new TranscriptionResult("Two.", "en"));
        await wait;
    });
}

public class RealtimeWireTests
{
    [Fact]
    public void Url() =>
        Assert.Equal("wss://api.openai.com/v1/realtime?intent=transcription", RealtimeWire.Url(OpenAIConfiguration.DefaultBaseUrl).AbsoluteUri);

    [Fact]
    public void SessionUpdate()
    {
        var update = JsonNode.Parse(RealtimeWire.SessionUpdate("gpt-live-transcribe", "tr"))!;
        Assert.Equal("transcription", update["session"]!["type"]!.GetValue<string>());
        var input = update["session"]!["audio"]!["input"]!;
        Assert.Equal(24_000, input["format"]!["rate"]!.GetValue<int>());
        Assert.Equal("tr", input["transcription"]!["languages"]![0]!.GetValue<string>());
        Assert.True(input.AsObject().ContainsKey("turn_detection"));
        Assert.Null(input["turn_detection"]);
    }

    [Fact]
    public void AppendEncodesPcm16()
    {
        var append = JsonNode.Parse(RealtimeWire.Append(new float[] { 0, 1, -1 }))!;
        Assert.Equal(new byte[] { 0x00, 0x00, 0xFF, 0x7F, 0x01, 0x80 }, Convert.FromBase64String(append["audio"]!.GetValue<string>()));
    }

    [Fact]
    public void ParsesServerEvents()
    {
        Assert.Equal(new RealtimeWire.ServerEvent.Committed("a"), RealtimeWire.Parse("""{"type":"input_audio_buffer.committed","item_id":"a"}"""));
        Assert.Equal(new RealtimeWire.ServerEvent.Completed("a", "Merhaba"),
            RealtimeWire.Parse("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"a","content_index":0,"transcript":"Merhaba"}"""));
        Assert.Equal(new RealtimeWire.ServerEvent.Error("bad"), RealtimeWire.Parse("""{"type":"error","error":{"message":"bad"}}"""));
        Assert.IsType<RealtimeWire.ServerEvent.Other>(RealtimeWire.Parse("""{"type":"session.updated"}"""));
        Assert.IsType<RealtimeWire.ServerEvent.Other>(RealtimeWire.Parse("not json"));
    }

    [Fact]
    public async Task FallsBackToFileModelWithoutLiveConnection()
    {
        var hub = new LiveTranscriptionHub(new ApiKeyProvider(new MemoryKeyStore()), () => OpenAIConfiguration.From(null));
        var service = new LiveFirstTranscriptionService(hub, new EchoModelTranscriber());
        var chunk = new AudioChunk { Source = AudioSourceKind.System, Samples = [], SampleRate = 16_000, StartedAt = DateTimeOffset.Now, EndedAt = DateTimeOffset.Now, Sequence = 1 };
        var result = await service.TranscribeAsync(chunk, new TranscriptionOptions("gpt-live-transcribe"), CancellationToken.None);
        Assert.Equal("gpt-transcribe", result.Text);
    }

    [Fact]
    public async Task LiveSessionWithoutKeyFailsFastToFileUpload()
    {
        var hub = new LiveTranscriptionHub(new ApiKeyProvider(new MemoryKeyStore()), () => OpenAIConfiguration.From(null));
        var sink = hub.Open(AudioSourceKind.System, "gpt-live-transcribe", null);
        var chunk = new AudioChunk { Source = AudioSourceKind.System, Samples = new float[1600], SampleRate = 16_000, StartedAt = DateTimeOffset.Now, EndedAt = DateTimeOffset.Now, Sequence = 1 };
        sink.Send(new LiveAudioEvent.Audio(chunk.Samples));
        sink.Send(new LiveAudioEvent.Commit(chunk.Id));
        var service = new LiveFirstTranscriptionService(hub, new EchoModelTranscriber()) { LiveTimeout = TimeSpan.FromSeconds(2) };
        var result = await service.TranscribeAsync(chunk, new TranscriptionOptions("gpt-live-transcribe"), CancellationToken.None);
        Assert.Equal("gpt-transcribe", result.Text);
        hub.CloseAll();
    }

    private sealed class EchoModelTranscriber : ITranscriptionService
    {
        public Task<TranscriptionResult> TranscribeAsync(AudioChunk chunk, TranscriptionOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new TranscriptionResult(options.Model, null));
    }
}

public class SettingsTests
{
    [Fact]
    public void PersistsAndReloads()
    {
        var path = Path.Combine(Path.GetTempPath(), "meetco-tests", Guid.NewGuid() + ".json");
        try
        {
            var settings = AppSettings.Load(path);
            Assert.Equal(ModelCatalog.DefaultAssistantModel, settings.ChatModel);
            Assert.Equal(ExplanationLanguage.Turkish, settings.ExplanationLanguage);
            settings.ChatModel = "gpt-4.1";
            settings.CaptureMode = AudioCaptureMode.SystemOnly;
            settings.UserContext = "Satış müdürüyüm.";

            var reloaded = AppSettings.Load(path);
            Assert.Equal("gpt-4.1", reloaded.ChatModel);
            Assert.Equal(AudioCaptureMode.SystemOnly, reloaded.CaptureMode);
            Assert.Equal("Satış müdürüyüm.", reloaded.UserContext);
            Assert.Contains("\"SystemOnly\"", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CorruptFileGivesDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "meetco-tests", Guid.NewGuid() + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        try
        {
            Assert.True(AppSettings.Load(path).AutoSuggest);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void KeyProviderCachesAndMasks()
    {
        var store = new MemoryKeyStore();
        var provider = new ApiKeyProvider(store);
        Assert.False(provider.HasKey);
        provider.Save("  sk-abcdefghijklmnop  ");
        Assert.Equal("sk-abcdefghijklmnop", store.Load());
        Assert.True(provider.HasKey);
        Assert.Equal("sk-…mnop", ApiKeyProvider.Mask("sk-abcdefghijklmnop"));
        provider.Delete();
        Assert.False(provider.HasKey);
    }
}
