using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;
using Meetco.Core.Settings;

namespace Meetco.Core.Anthropic;

public interface IAnthropicClient : IModelLister
{
    /// <summary>Streams the text deltas of a Messages API response.</summary>
    IAsyncEnumerable<string> StreamMessageAsync(AnthropicMessageRequest request, CancellationToken cancellationToken);
}

/// <summary>A Messages API request, converted from the OpenAI-style messages <see cref="Assistant.PromptBuilder"/> builds.</summary>
public sealed record AnthropicMessageRequest
{
    public required string Model { get; init; }
    public required string System { get; init; }
    /// <summary>Each message: role plus content blocks (text, or a base64 JPEG).</summary>
    public required IReadOnlyList<(string Role, IReadOnlyList<ContentPart> Blocks)> Messages { get; init; }
    public required int MaxTokens { get; init; }
    public double? Temperature { get; init; }

    public static AnthropicMessageRequest From(string model, IReadOnlyList<ChatMessage> chatMessages, int maxTokens, double? temperature)
    {
        var system = new List<string>();
        var messages = new List<(string, IReadOnlyList<ContentPart>)>();
        foreach (var message in chatMessages)
        {
            IReadOnlyList<ContentPart> blocks = message.Parts ?? [ContentPart.FromText(message.Text ?? "")];
            if (message.Role == "system") system.AddRange(blocks.Where(b => b.Text is not null).Select(b => b.Text!));
            else messages.Add((message.Role, blocks));
        }
        return new AnthropicMessageRequest
        {
            Model = model,
            System = string.Join("\n\n", system),
            Messages = messages,
            MaxTokens = maxTokens,
            Temperature = temperature,
        };
    }

    public string ToJsonString()
    {
        var json = new JsonObject
        {
            ["model"] = Model,
            ["system"] = System,
            ["max_tokens"] = MaxTokens,
            ["stream"] = true,
            ["messages"] = new JsonArray(Messages.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = new JsonArray(m.Blocks.Select(Block).Where(b => b is not null).ToArray()),
            }).ToArray()),
        };
        if (Temperature is { } temperature) json["temperature"] = temperature;
        return json.ToJsonString(Json.Options);
    }

    private static JsonNode? Block(ContentPart part)
    {
        if (part.Type != "image_url") return new JsonObject { ["type"] = "text", ["text"] = part.Text ?? "" };
        var url = part.ImageUrl ?? "";
        var comma = url.IndexOf(',');
        if (comma < 0) return null;
        return new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = url[(comma + 1)..] },
        };
    }
}

/// <summary>
/// Minimal client for Anthropic's Messages API on HttpClient. Errors are raised as
/// <see cref="OpenAIException"/> so retry and messaging behave the same for every provider.
/// </summary>
public sealed class AnthropicClient : IAnthropicClient, IDisposable
{
    public const string ApiVersion = "2023-06-01";
    private const int MaxRetries = 2;

    private readonly ApiKeyProvider _keyProvider;
    private readonly Func<OpenAIConfiguration> _configuration;
    private readonly HttpClient _http;

    public TimeSpan StreamIdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public AnthropicClient(ApiKeyProvider keyProvider, Func<OpenAIConfiguration> configuration, HttpMessageHandler? handler = null)
    {
        _keyProvider = keyProvider;
        _configuration = configuration;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Meetco/1.0 (Windows)");
    }

    public void Dispose() => _http.Dispose();

    public async IAsyncEnumerable<string> StreamMessageAsync(AnthropicMessageRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = request.ToJsonString();
        var attempt = 0;
        while (true)
        {
            using var response = await SendAsync(
                () => MakeRequest("messages", HttpMethod.Post, new StringContent(body, Encoding.UTF8, "application/json"), null),
                cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var yielded = false;
            OpenAIException? retryable = null;
            while (true)
            {
                string? line;
                try
                {
                    idle.CancelAfter(StreamIdleTimeout);
                    line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw OpenAIException.Network("The response stopped arriving (timed out).");
                }
                catch (IOException error) when (!cancellationToken.IsCancellationRequested)
                {
                    throw OpenAIException.Network(error.Message);
                }
                if (line is null) yield break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                StreamEvent? streamEvent;
                try
                {
                    streamEvent = JsonSerializer.Deserialize<StreamEvent>(line[5..].Trim(), Json.Options);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (streamEvent is null) continue;
                if (streamEvent.Type == "content_block_delta" && streamEvent.Delta is { Type: "text_delta", Text: { Length: > 0 } text })
                {
                    yielded = true;
                    yield return text;
                }
                else if (streamEvent.Type == "error")
                {
                    // e.g. overloaded_error mid-stream; retryable only if nothing was shown yet.
                    var status = streamEvent.Error?.Type == "overloaded_error" ? 529 : 500;
                    var error = OpenAIException.Http(status, streamEvent.Error?.Message ?? "Stream error", streamEvent.Error?.Type);
                    if (yielded || attempt >= MaxRetries) throw error;
                    retryable = error;
                    break;
                }
                else if (streamEvent.Type == "message_stop")
                {
                    yield break;
                }
            }
            attempt += 1;
            Log.Notice("network", $"Retrying Anthropic stream after {retryable?.Detail}");
            await Task.Delay(TimeSpan.FromSeconds(0.8 * Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(string? apiKeyOverride, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => MakeRequest("models?limit=100", HttpMethod.Get, null, apiKeyOverride), cancellationToken, retry: false)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = JsonSerializer.Deserialize<ModelList>(body, Json.Options);
            return (list?.Data ?? []).Select(m => m.Id).Order(StringComparer.Ordinal).ToList();
        }
        catch (JsonException error)
        {
            throw OpenAIException.Decoding(error.Message);
        }
    }

    private HttpRequestMessage MakeRequest(string path, HttpMethod method, HttpContent? content, string? apiKeyOverride)
    {
        var apiKey = (apiKeyOverride ?? _keyProvider.CurrentKey())?.Trim();
        if (string.IsNullOrEmpty(apiKey)) throw OpenAIException.MissingApiKey();
        var request = new HttpRequestMessage(method, _configuration().Endpoint(path)) { Content = content };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);
        return request;
    }

    /// <summary>Sends with retries for transient failures before any content arrived.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> makeRequest, CancellationToken cancellationToken, bool retry = true)
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = makeRequest();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_configuration().RequestTimeout);
                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw OpenAIException.Network("The request timed out.");
                }
                catch (HttpRequestException error)
                {
                    throw OpenAIException.Network(error.Message);
                }
                if (response.IsSuccessStatusCode) return response;
                using (response)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    // Anthropic errors look like {"type":"error","error":{"type":"…","message":"…"}}.
                    var envelope = TryParseError(body);
                    Log.Error("network", $"HTTP {(int)response.StatusCode} from Anthropic");
                    throw OpenAIException.Http((int)response.StatusCode, envelope?.Message ?? response.ReasonPhrase ?? "HTTP error",
                        envelope?.Type, response.Headers.RetryAfter?.Delta);
                }
            }
            catch (OpenAIException error) when (retry && error.IsRetryable && attempt < MaxRetries)
            {
                attempt += 1;
                var delay = error.RetryAfter?.TotalSeconds is { } after ? Math.Min(after, 8) : 0.8 * Math.Pow(2, attempt - 1);
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static ErrorBody? TryParseError(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<ErrorEnvelope>(body, Json.Options)?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class StreamEvent
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("delta")] public DeltaBody? Delta { get; set; }
        [JsonPropertyName("error")] public ErrorBody? Error { get; set; }
    }

    private sealed class DeltaBody
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }

    private sealed class ErrorEnvelope
    {
        [JsonPropertyName("error")] public ErrorBody? Error { get; set; }
    }

    private sealed class ErrorBody
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class ModelList
    {
        [JsonPropertyName("data")] public List<ModelEntry> Data { get; set; } = [];
    }

    private sealed class ModelEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
    }
}
