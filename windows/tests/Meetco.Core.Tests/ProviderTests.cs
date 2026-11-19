using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Meetco.Core.Anthropic;
using Meetco.Core.Assistant;
using Meetco.Core.Audio;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;
using Meetco.Core.Settings;
using Meetco.Core.Transcription;
using Xunit.Abstractions;

namespace Meetco.Core.Tests;

internal sealed class FakeAnthropicClient(params object[] responses) : IAnthropicClient
{
    private readonly Queue<object> _responses = new(responses);
    public List<AnthropicMessageRequest> Requests { get; } = [];

    public async IAsyncEnumerable<string> StreamMessageAsync(AnthropicMessageRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var next = _responses.Dequeue();
        if (next is Exception error) throw error;
        foreach (var word in ((string)next).Split(' '))
        {
            await Task.Yield();
            yield return word + " ";
        }
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(string? apiKeyOverride, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>Records requests and answers with a fixed status and body.</summary>
internal sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    }
}

public class ProviderTests
{
    private static readonly ScreenContext Screen = new(new byte[] { 0xFF, 0xD8, 0xFF }, DateTimeOffset.Now, 1, 1);

    private static AssistantRequest Request(AssistantProvider provider, SuggestionKind kind = SuggestionKind.Answer, string model = "m", bool screen = false) => new()
    {
        Kind = kind,
        FocusQuestion = "When can we launch?",
        Transcript = [new TranscriptLine(AudioSourceKind.System, "When can we launch?", "en", DateTimeOffset.Now)],
        Preferences = new AssistantPreferences(ResponseLanguage.Auto, ExplanationLanguage.None, ResponseStyle.Concise, ""),
        Model = model,
        ScreenContext = screen ? Screen : null,
        Provider = provider,
    };

    // MARK: OpenAI-compatible request shaping

    [Fact]
    public void DeepSeekUsesJsonModeMaxTokensAndNoImage()
    {
        var chat = OpenAIMeetingAssistant.ChatRequest(Request(AssistantProvider.DeepSeek, SuggestionKind.Recap, screen: true));
        Assert.Equal("json_object", chat.ResponseFormat?.Type);
        Assert.Equal(900, chat.MaxTokens);
        Assert.Null(chat.MaxCompletionTokens);
        Assert.Null(chat.ReasoningEffort);
        Assert.DoesNotContain(chat.Messages, m => m.HasImage);
        var body = chat.ToJsonString();
        Assert.Contains("key_points", body);
        Assert.Contains("\"max_tokens\":900", body);
    }

    [Fact]
    public void OpenAIRequestIsUnchanged()
    {
        var chat = OpenAIMeetingAssistant.ChatRequest(Request(AssistantProvider.OpenAI, SuggestionKind.Recap, "gpt-6.1-terra", screen: true));
        Assert.Equal("json_schema", chat.ResponseFormat?.Type);
        Assert.Equal(4000, chat.MaxCompletionTokens);
        Assert.Null(chat.MaxTokens);
        Assert.Equal("none", chat.ReasoningEffort);
        Assert.Contains(chat.Messages, m => m.HasImage);
    }

    [Fact]
    public async Task LocalModelDegradesStructuredOutputStepByStep()
    {
        var client = new FakeOpenAIClient(
            OpenAIException.Http(400, "response_format json_schema is not supported"),
            OpenAIException.Http(400, "'response_format.type' must be 'json_schema' or 'text'"),
            """{"summary": "We talked.", "key_points": [], "action_items": [], "open_questions": [], "language": "English", "meeting_topic": "Sync"}""");
        var output = await new OpenAIMeetingAssistant(client).GenerateAsync(Request(AssistantProvider.Ollama, SuggestionKind.Recap), _ => { }, CancellationToken.None);
        Assert.Equal("We talked.", output.Text);
        Assert.Equal(["json_schema", "json_object", null], client.Requests.Select(r => r.ResponseFormat?.Type));
    }

    [Fact]
    public async Task LocalModelDropsScreenshotWhenImagesUnsupported()
    {
        var client = new FakeOpenAIClient(OpenAIException.Http(400, "this model does not support image input"), "Next month.");
        var output = await new OpenAIMeetingAssistant(client).GenerateAsync(Request(AssistantProvider.LMStudio, screen: true), _ => { }, CancellationToken.None);
        Assert.Equal("Next month.", output.Text);
        Assert.Contains(client.Requests[0].Messages, m => m.HasImage);
        Assert.DoesNotContain(client.Requests[1].Messages, m => m.HasImage);
    }

    [Fact]
    public async Task LocalServersGetNoAuthorizationHeader()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"data":[{"id":"qwen3"}]}""");
        var keys = new ApiKeyProvider(new MemoryKeyStore());
        var local = new OpenAIClient(keys, () => OpenAIConfiguration.For(AssistantProvider.Ollama, null), handler: handler, requiresApiKey: false);
        Assert.Equal(["qwen3"], await local.ListModelsAsync(null, CancellationToken.None));
        Assert.Null(handler.Requests[0].Headers.Authorization);
        Assert.Equal("http://localhost:11434/v1/models", handler.Requests[0].RequestUri!.AbsoluteUri);

        var hosted = new OpenAIClient(keys, () => OpenAIConfiguration.From(null), handler: handler);
        var error = await Assert.ThrowsAsync<OpenAIException>(() => hosted.ListModelsAsync(null, CancellationToken.None));
        Assert.Equal(OpenAIErrorKind.MissingApiKey, error.Kind);
    }

    // MARK: Anthropic

    [Fact]
    public void AnthropicRequestShape()
    {
        var message = AnthropicMeetingAssistant.MessageRequest(Request(AssistantProvider.Anthropic, SuggestionKind.Recap, "claude-haiku-5-5", screen: true));
        var json = JsonNode.Parse(message.ToJsonString())!;
        Assert.Equal("claude-haiku-5-5", (string?)json["model"]);
        Assert.Equal(1200, (int?)json["max_tokens"]);
        Assert.True((bool?)json["stream"]);
        Assert.Contains("key_points", (string?)json["system"]);
        var content = json["messages"]![0]!["content"]!.AsArray();
        var image = content.First(b => (string?)b!["type"] == "image")!;
        Assert.Equal("image/jpeg", (string?)image["source"]!["media_type"]);
        Assert.Equal(Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF }), (string?)image["source"]!["data"]);
    }

    [Fact]
    public async Task AnthropicClientStreamsTextDeltasWithHeaders()
    {
        var sse = string.Join("\n",
            "event: message_start", """data: {"type":"message_start","message":{"id":"m"}}""", "",
            "event: content_block_delta", """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Next "}}""", "",
            "event: content_block_delta", """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"month."}}""", "",
            "event: message_stop", """data: {"type":"message_stop"}""", "");
        var handler = new RecordingHandler(HttpStatusCode.OK, sse);
        using var client = new AnthropicClient(new ApiKeyProvider(new MemoryKeyStore("sk-ant-test")),
            () => OpenAIConfiguration.For(AssistantProvider.Anthropic, null), handler);
        var output = await new AnthropicMeetingAssistant(client).GenerateAsync(Request(AssistantProvider.Anthropic), _ => { }, CancellationToken.None);
        Assert.Equal("Next month.", output.Text);
        var request = handler.Requests.Single();
        Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.AbsoluteUri);
        Assert.Equal("sk-ant-test", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal(AnthropicClient.ApiVersion, request.Headers.GetValues("anthropic-version").Single());
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task AnthropicErrorsBecomeProviderMessages()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");
        using var client = new AnthropicClient(new ApiKeyProvider(new MemoryKeyStore("bad")), () => OpenAIConfiguration.For(AssistantProvider.Anthropic, null), handler);
        var router = new ProviderRoutingAssistant(new Dictionary<AssistantProvider, IOpenAIClient>(), client);
        var error = await Assert.ThrowsAsync<ProviderException>(() => router.GenerateAsync(Request(AssistantProvider.Anthropic), _ => { }, CancellationToken.None));
        Assert.Contains("Anthropic rejected the API key", error.Message);
        Assert.Contains("Settings → Assistant", error.Message);
    }

    [Fact]
    public async Task AnthropicDropsTemperatureWhenRejected()
    {
        var client = new FakeAnthropicClient(OpenAIException.Http(400, "temperature is not supported for this model"), "Soon.");
        var output = await new AnthropicMeetingAssistant(client).GenerateAsync(Request(AssistantProvider.Anthropic), _ => { }, CancellationToken.None);
        Assert.Equal("Soon.", output.Text);
        Assert.Equal(0.5, client.Requests[0].Temperature);
        Assert.Null(client.Requests[1].Temperature);
    }

    // MARK: Routing, settings, keys

    [Fact]
    public async Task RouterPicksClientAndNamesProvider()
    {
        var deepSeek = new FakeOpenAIClient("Yes.", OpenAIException.Http(401, "bad key"));
        var router = new ProviderRoutingAssistant(new Dictionary<AssistantProvider, IOpenAIClient> { [AssistantProvider.DeepSeek] = deepSeek },
            new FakeAnthropicClient("Sure."));
        Assert.Equal("Yes.", (await router.GenerateAsync(Request(AssistantProvider.DeepSeek), _ => { }, CancellationToken.None)).Text);
        Assert.Equal("Sure.", (await router.GenerateAsync(Request(AssistantProvider.Anthropic), _ => { }, CancellationToken.None)).Text);
        var error = await Assert.ThrowsAsync<ProviderException>(() => router.GenerateAsync(Request(AssistantProvider.DeepSeek), _ => { }, CancellationToken.None));
        Assert.Contains("DeepSeek rejected the API key", error.Message);
    }

    [Fact]
    public void LocalNetworkErrorsExplainWhatToCheck()
    {
        var error = new ProviderException(AssistantProvider.Ollama, OpenAIException.Network("Connection refused"));
        Assert.Contains("Can't reach Ollama at http://localhost:11434/v1", error.Message);
    }

    [Fact]
    public void SettingsKeepModelAndUrlPerProvider()
    {
        var path = Path.Combine(Path.GetTempPath(), "meetco-" + Guid.NewGuid() + ".json");
        try
        {
            var settings = AppSettings.Load(path);
            Assert.Equal(settings.ChatModel, settings.AssistantModel);
            settings.AssistantProvider = AssistantProvider.DeepSeek;
            Assert.Equal("deepseek-chat", settings.AssistantModel);
            settings.AssistantModel = "deepseek-reasoner";
            settings.SetBaseUrl(AssistantProvider.Ollama, "http://gpu-box:11434/v1");
            settings.TranscriptionProvider = TranscriptionProvider.OnDevice;
            settings.WhisperModel = WhisperModelSize.LargeTurbo;
            Assert.Equal(GlassTint.Neutral, settings.GlassTint);
            settings.GlassTint = GlassTint.Teal;

            var reloaded = AppSettings.Load(path);
            Assert.Equal(AssistantProvider.DeepSeek, reloaded.AssistantProvider);
            Assert.Equal("deepseek-reasoner", reloaded.AssistantModel);
            Assert.NotEqual("deepseek-reasoner", reloaded.ChatModel);
            Assert.Equal("http://gpu-box:11434/v1", reloaded.ConfigurationFor(AssistantProvider.Ollama).BaseUrl.AbsoluteUri);
            Assert.Equal(AssistantProvider.LMStudio.DefaultBaseUrl(), reloaded.ConfigurationFor(AssistantProvider.LMStudio).BaseUrl);
            Assert.Equal(TranscriptionProvider.OnDevice, reloaded.TranscriptionProvider);
            Assert.Equal(WhisperModelSize.LargeTurbo, reloaded.WhisperModel);
            Assert.Equal(GlassTint.Teal, reloaded.GlassTint);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void KeyRegistryReadiness()
    {
        var registry = new ApiKeyRegistry(new ApiKeyProvider(new MemoryKeyStore("sk-openai")),
            provider => new MemoryKeyStore(provider == AssistantProvider.Anthropic ? "sk-ant-123" : null));
        Assert.True(registry.IsReady(AssistantProvider.OpenAI));
        Assert.True(registry.IsReady(AssistantProvider.Anthropic));
        Assert.False(registry.IsReady(AssistantProvider.DeepSeek));
        Assert.True(registry.IsReady(AssistantProvider.Ollama));
        Assert.Equal("Meetco/OpenAI-API-Key", AssistantProvider.OpenAI.CredentialTarget());
        Assert.Equal("Meetco/DeepSeek-API-Key", AssistantProvider.DeepSeek.CredentialTarget());
    }
}

public class WhisperTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RouterUsesChosenProvider()
    {
        var router = new TranscriptionRouter(new FixedTranscriber("cloud"), new FixedTranscriber("local"));
        var chunk = new AudioChunk { Source = AudioSourceKind.System, SampleRate = 16_000, Samples = [], StartedAt = DateTimeOffset.Now, EndedAt = DateTimeOffset.Now, Sequence = 0 };
        Assert.Equal("cloud", (await router.TranscribeAsync(chunk, new TranscriptionOptions("m"), CancellationToken.None)).Text);
        Assert.Equal("local", (await router.TranscribeAsync(chunk, new TranscriptionOptions("m", Provider: TranscriptionProvider.OnDevice), CancellationToken.None)).Text);
    }

    /// <summary>Loads whisper.cpp's native library (on CI this checks the Windows runtime ships and loads).</summary>
    [Fact]
    public void NativeRuntimeLoads()
    {
        var info = Whisper.net.WhisperFactory.GetRuntimeInfo();
        output.WriteLine(info);
        Assert.False(string.IsNullOrWhiteSpace(info));
        Assert.Contains("tr", Whisper.net.WhisperFactory.GetSupportedLanguages());
    }

    [Fact]
    public async Task MissingModelGivesActionableError()
    {
        var store = new WhisperModelStore(Path.Combine(Path.GetTempPath(), "meetco-empty-" + Guid.NewGuid()));
        using var service = new WhisperTranscriptionService(store, () => WhisperModelSize.Small);
        var error = await Assert.ThrowsAsync<OnDeviceTranscriptionException>(() => service.PrepareAsync(CancellationToken.None));
        Assert.Contains("Settings → Audio", error.Message);
    }

    /// <summary>
    /// Runs Whisper on real speech. Needs MEETCO_WHISPER_MODELS pointing at a folder with ggml-small-q5_1.bin,
    /// and macOS (the speech is synthesized with `say`); otherwise it returns without checking anything.
    /// </summary>
    [Theory]
    [InlineData("Samantha", "en", "When can we launch the new product?", "launch")]
    [InlineData("Yelda", "tr", "Yeni ürünü ne zaman piyasaya sürebiliriz?", "ürün")]
    public async Task TranscribesRealSpeech(string voice, string language, string phrase, string expected)
    {
        var folder = Environment.GetEnvironmentVariable("MEETCO_WHISPER_MODELS");
        if (folder is null || !OperatingSystem.IsMacOS())
        {
            output.WriteLine("Skipped: set MEETCO_WHISPER_MODELS on macOS to run.");
            return;
        }
        var store = new WhisperModelStore(folder);
        Assert.True(store.IsInstalled(WhisperModelSize.Small));
        using var service = new WhisperTranscriptionService(store, () => WhisperModelSize.Small);
        await service.PrepareAsync(CancellationToken.None);

        var chunk = new AudioChunk { Source = AudioSourceKind.System, SampleRate = 16_000, Samples = Synthesize(voice, phrase), StartedAt = DateTimeOffset.Now, EndedAt = DateTimeOffset.Now, Sequence = 0 };
        foreach (var languageHint in new[] { language, null })
        {
            var watch = Stopwatch.StartNew();
            var result = await service.TranscribeAsync(chunk, new TranscriptionOptions("", languageHint, Provider: TranscriptionProvider.OnDevice), CancellationToken.None);
            output.WriteLine($"Whisper ({languageHint ?? "auto"}): \"{result.Text}\" [{result.LanguageCode}] in {watch.Elapsed.TotalSeconds:0.00}s");
            Console.WriteLine($"Whisper ({languageHint ?? "auto"}): \"{result.Text}\" [{result.LanguageCode}] in {watch.Elapsed.TotalSeconds:0.00}s");
            Assert.Contains(expected, result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(language, result.LanguageCode);
        }
    }

    private static float[] Synthesize(string voice, string phrase)
    {
        var directory = Directory.CreateTempSubdirectory("meetco-say-").FullName;
        try
        {
            var aiff = Path.Combine(directory, "speech.aiff");
            var raw = Path.Combine(directory, "speech.raw");
            Run("/usr/bin/say", "-v", voice, "-o", aiff, phrase);
            // Headerless little-endian float32, 16 kHz mono.
            Run("/usr/bin/afconvert", "-f", "WAVE", "-d", "LEF32@16000", "-c", "1", aiff, raw);
            var bytes = File.ReadAllBytes(raw);
            // Skip the WAV header: find the "data" chunk.
            var index = 12;
            while (index + 8 <= bytes.Length)
            {
                var id = Encoding.ASCII.GetString(bytes, index, 4);
                var size = BitConverter.ToInt32(bytes, index + 4);
                if (id == "data")
                {
                    var samples = new float[size / 4];
                    Buffer.BlockCopy(bytes, index + 8, samples, 0, samples.Length * 4);
                    return samples;
                }
                index += 8 + size;
            }
            throw new InvalidOperationException("No audio data");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Run(string tool, params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(tool, arguments) { UseShellExecute = false })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class FixedTranscriber(string text) : ITranscriptionService
    {
        public Task<TranscriptionResult> TranscribeAsync(AudioChunk chunk, TranscriptionOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new TranscriptionResult(text, null));
    }
}
