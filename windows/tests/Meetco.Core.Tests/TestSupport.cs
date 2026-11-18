using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Meetco.Core.Assistant;
using Meetco.Core.Audio;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;
using Meetco.Core.Settings;

namespace Meetco.Core.Tests;

internal static class Signals
{
    public const int SampleRate = 16_000;

    public static float[] Tone(double seconds, float amplitude = 0.2f) =>
        Enumerable.Range(0, (int)(SampleRate * seconds))
            .Select(i => amplitude * MathF.Sin(i * 2 * MathF.PI * 220 / SampleRate))
            .ToArray();

    public static float[] Silence(double seconds) => new float[(int)(SampleRate * seconds)];

    public static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();
}

internal sealed class MemoryKeyStore(string? key = null) : IApiKeyStore
{
    private string? _key = key;
    public string? Load() => _key;
    public void Save(string key) => _key = key;
    public void Delete() => _key = null;
}

/// <summary>Fake OpenAI client that streams canned chat content in small pieces and records requests.</summary>
internal sealed class FakeOpenAIClient(params object[] responses) : IOpenAIClient
{
    private readonly Queue<object> _responses = new(responses);
    public List<ChatCompletionRequest> Requests { get; } = [];

    public async IAsyncEnumerable<string> StreamChatCompletionAsync(ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var next = _responses.Dequeue();
        if (next is Exception error) throw error;
        var content = (string)next;
        for (var index = 0; index < content.Length; index += 7)
        {
            await Task.Yield();
            yield return content.Substring(index, Math.Min(7, content.Length - index));
        }
    }

    public Task<TranscriptionResponse> CreateTranscriptionAsync(byte[] audio, string filename, TranscriptionOptions options, bool verbose, CancellationToken cancellationToken) =>
        Task.FromResult(new TranscriptionResponse { Text = "" });

    public Task<IReadOnlyList<string>> ListModelsAsync(string? apiKeyOverride, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>Assistant that waits for the test to release each request.</summary>
internal sealed class ScriptedAssistant : IMeetingAssistant
{
    public List<AssistantRequest> Requests { get; } = [];
    public Func<AssistantRequest, AssistantOutput> Respond { get; set; } =
        request => new AssistantOutput { Text = "Answer to " + request.FocusQuestion };

    public Task<AssistantOutput> GenerateAsync(AssistantRequest request, Action<AssistantPartial> onPartial, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Respond(request));
    }
}

/// <summary>
/// Runs async code on a single thread with a SynchronizationContext, like the WPF UI thread,
/// so Core's UI-thread-only classes are exercised the way the app uses them.
/// </summary>
internal static class SingleThread
{
    public static void Run(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        var context = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var task = body();
            task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.RunUntilComplete();
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class PumpContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // The test already finished; late timers are irrelevant.
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public void Complete() => _queue.CompleteAdding();

        public void RunUntilComplete()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        }
    }
}
