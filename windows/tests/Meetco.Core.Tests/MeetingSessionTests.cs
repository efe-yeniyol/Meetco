using Meetco.Core.Assistant;
using Meetco.Core.Audio;
using Meetco.Core.Models;
using Meetco.Core.Session;
using Meetco.Core.Settings;
using Meetco.Core.Transcription;
using static Meetco.Core.Tests.Signals;

namespace Meetco.Core.Tests;

public class MeetingSessionTests
{
    private sealed class FakeCapture(AudioSourceKind kind, bool fails = false) : IAudioCapture
    {
        public AudioSourceKind Kind { get; } = kind;
        public Action<float[]>? OnSamples { get; private set; }
        public Action<Exception?>? OnStopped { get; private set; }
        public bool IsRunning { get; private set; }

        public void Start(Action<float[]> onSamples, Action<Exception?> onStopped)
        {
            if (fails) throw new InvalidOperationException($"{Kind} unavailable");
            OnSamples = onSamples;
            OnStopped = onStopped;
            IsRunning = true;
        }

        public void Stop() => IsRunning = false;
        public void Dispose() => IsRunning = false;
    }

    private sealed class FakeFactory : IAudioCaptureFactory
    {
        public bool SystemFails { get; set; }
        public bool MicrophoneAllowed { get; set; } = true;
        public List<FakeCapture> Created { get; } = [];

        public IAudioCapture CreateSystemCapture() => Track(new FakeCapture(AudioSourceKind.System, SystemFails));
        public IAudioCapture CreateMicrophoneCapture(string? deviceId) => Track(new FakeCapture(AudioSourceKind.Microphone));

        public bool CanUseMicrophone(out string? problem)
        {
            problem = MicrophoneAllowed ? null : "Microphone access is off.";
            return MicrophoneAllowed;
        }

        private FakeCapture Track(FakeCapture capture)
        {
            Created.Add(capture);
            return capture;
        }
    }

    private sealed class EchoTranscriber : ITranscriptionService
    {
        public int Calls;

        public Task<Meetco.Core.OpenAI.TranscriptionResult> TranscribeAsync(AudioChunk chunk, Meetco.Core.OpenAI.TranscriptionOptions options, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new Meetco.Core.OpenAI.TranscriptionResult($"Can you explain part {Calls}?", "en"));
        }
    }

    private sealed class Harness
    {
        public FakeFactory Factory { get; } = new();
        public AppSettings Settings { get; } = new() { TranscriptionModel = "gpt-transcribe" };
        public TranscriptStore Store { get; } = new();
        public ScriptedAssistant Assistant { get; } = new();
        public EchoTranscriber Transcriber { get; } = new();
        public SuggestionEngine Suggestions { get; }
        public MeetingSessionController Session { get; }

        public Harness(string? key = "sk-test")
        {
            var keys = new ApiKeyProvider(new MemoryKeyStore(key));
            var transcription = new TranscriptionCoordinator(Transcriber, Store, Settings);
            Suggestions = new SuggestionEngine(Assistant, null, Store, Settings, keys);
            Session = new MeetingSessionController(Settings, Factory, keys, transcription, Store, Suggestions);
        }
    }

    [Fact]
    public void RequiresApiKey() => SingleThread.Run(async () =>
    {
        var harness = new Harness(key: null);
        harness.Session.Start();
        await Task.Yield();
        Assert.Equal(SessionState.Idle, harness.Session.State);
        Assert.Contains("API key", harness.Session.ErrorMessage);
        Assert.Empty(harness.Factory.Created);
    });

    [Fact]
    public void SpeechFlowsFromCaptureToAnswer() => SingleThread.Run(async () =>
    {
        var harness = new Harness();
        harness.Session.Start();
        await Task.Delay(20);
        Assert.Equal(SessionState.Listening, harness.Session.State);
        Assert.Equal(2, harness.Factory.Created.Count);

        var system = harness.Factory.Created.Single(c => c.Kind == AudioSourceKind.System);
        system.OnSamples!(Tone(1.5));
        system.OnSamples!(Silence(1.2));
        await Task.Delay(100);

        var segment = Assert.Single(harness.Store.Segments);
        Assert.Equal("Can you explain part 1?", segment.Text);
        Assert.True(segment.IsQuestion);
        Assert.Single(harness.Assistant.Requests);
        Assert.Single(harness.Suggestions.Suggestions);
        Assert.True(harness.Session.SystemLevel > 0);

        harness.Session.Stop();
        await Task.Delay(20);
        Assert.Equal(SessionState.Idle, harness.Session.State);
        Assert.All(harness.Factory.Created, c => Assert.False(c.IsRunning));
    });

    [Fact]
    public void MicrophoneSpeechIsTranscribedButNotAnswered() => SingleThread.Run(async () =>
    {
        var harness = new Harness();
        harness.Session.Start();
        await Task.Delay(20);
        var microphone = harness.Factory.Created.Single(c => c.Kind == AudioSourceKind.Microphone);
        microphone.OnSamples!(Concat(Tone(1.5), Silence(1.2)));
        await Task.Delay(100);
        Assert.Single(harness.Store.Segments);
        Assert.Empty(harness.Assistant.Requests);
    });

    [Fact]
    public void MutedMicrophoneIsIgnored() => SingleThread.Run(async () =>
    {
        var harness = new Harness();
        harness.Session.IsMicrophoneMuted = true;
        harness.Session.Start();
        await Task.Delay(20);
        harness.Factory.Created.Single(c => c.Kind == AudioSourceKind.Microphone).OnSamples!(Concat(Tone(1.5), Silence(1.2)));
        await Task.Delay(100);
        Assert.Empty(harness.Store.Segments);
    });

    [Fact]
    public void FailedSourceBecomesWarningWhenAnotherWorks() => SingleThread.Run(async () =>
    {
        var harness = new Harness();
        harness.Factory.SystemFails = true;
        harness.Session.Start();
        await Task.Delay(20);
        Assert.Equal(SessionState.Listening, harness.Session.State);
        Assert.Contains("System unavailable", harness.Session.WarningMessage);
        Assert.Equal(new[] { AudioSourceKind.Microphone }, harness.Session.ActiveSources);
        harness.Session.Stop();
        await Task.Delay(20);
    });

    [Fact]
    public void NoWorkingSourceIsAnError() => SingleThread.Run(async () =>
    {
        var harness = new Harness();
        harness.Factory.SystemFails = true;
        harness.Factory.MicrophoneAllowed = false;
        harness.Session.Start();
        await Task.Delay(20);
        Assert.Equal(SessionState.Idle, harness.Session.State);
        Assert.Contains("Microphone access is off.", harness.Session.ErrorMessage);
    });

    [Fact]
    public void SystemInterruptionReconnects() => SingleThread.Run(async () =>
    {
        var harness = new Harness();
        harness.Settings.CaptureMode = AudioCaptureMode.SystemOnly;
        harness.Session.Start();
        await Task.Delay(20);
        var first = harness.Factory.Created.Single();
        first.OnStopped!(new InvalidOperationException("device changed"));
        await Task.Delay(1300);
        Assert.Equal(2, harness.Factory.Created.Count);
        Assert.True(harness.Factory.Created[1].IsRunning);
        Assert.Null(harness.Session.WarningMessage);
        harness.Session.Stop();
        await Task.Delay(20);
    });
}
