using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;
using Meetco.Core.Settings;

namespace Meetco.Core.OpenAI;

/// <summary>Abstraction over the OpenAI REST API so services can be tested with fakes.</summary>
public interface IOpenAIClient
{
    /// <summary>Streams content deltas of a chat completion (server-sent events).</summary>
    IAsyncEnumerable<string> StreamChatCompletionAsync(ChatCompletionRequest request, CancellationToken cancellationToken);

    Task<TranscriptionResponse> CreateTranscriptionAsync(byte[] audio, string filename, TranscriptionOptions options, bool verbose, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListModelsAsync(string? apiKeyOverride, CancellationToken cancellationToken);
}

/// <summary>
/// Thin, dependency-free OpenAI client built on HttpClient.
/// Uses the user's own API key, retries transient failures (429, 5xx, network) with exponential
/// backoff, and never logs credentials or request bodies.
/// </summary>
public sealed class OpenAIClient : IOpenAIClient, IDisposable
{
    public sealed record RetryPolicy(int MaxRetries = 2, double BaseDelaySeconds = 0.8, double MaxDelaySeconds = 8);

    private readonly ApiKeyProvider _keyProvider;
    private readonly Func<OpenAIConfiguration> _configuration;
    private readonly RetryPolicy _retryPolicy;
    private readonly HttpClient _http;

    /// <summary>Longest silence tolerated in the middle of a streamed response.</summary>
    public TimeSpan StreamIdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public OpenAIClient(ApiKeyProvider keyProvider, Func<OpenAIConfiguration> configuration, RetryPolicy? retryPolicy = null, HttpMessageHandler? handler = null)
    {
        _keyProvider = keyProvider;
        _configuration = configuration;
        _retryPolicy = retryPolicy ?? new RetryPolicy();
        var socketsHandler = handler ?? new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(socketsHandler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Meetco/1.0 (Windows)");
    }

    public void Dispose() => _http.Dispose();

    // MARK: Endpoints

    public async IAsyncEnumerable<string> StreamChatCompletionAsync(ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = (request with { Stream = true }).ToJsonString();
        using var response = await SendWithRetryAsync(
            () => MakeRequest("chat/completions", HttpMethod.Post, new StringContent(body, Encoding.UTF8, "application/json"), null),
            HttpCompletionOption.ResponseHeadersRead,
            retry: true,
            cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // A dropped connection can leave the stream silent forever: fail if no data arrives for a while.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var idleTimeout = StreamIdleTimeout;
        while (true)
        {
            string? line;
            try
            {
                idle.CancelAfter(idleTimeout);
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
            var payload = line[5..].Trim();
            if (payload == "[DONE]") yield break;

            ChatCompletionChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatCompletionChunk>(payload, Json.Options);
            }
            catch (JsonException)
            {
                continue;
            }
            var choice = chunk?.Choices.FirstOrDefault();
            if (choice?.Delta is not { } delta) continue;
            if (!string.IsNullOrEmpty(delta.Refusal)) throw OpenAIException.Http(200, "Refused: " + delta.Refusal, "refusal");
            if (!string.IsNullOrEmpty(delta.Content)) yield return delta.Content;
        }
    }

    /// <summary>Opens a connection to the API host ahead of time so the first real request is faster.</summary>
    public async Task WarmUpAsync()
    {
        if (!_keyProvider.HasKey) return;
        try
        {
            await ListModelsAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best effort only.
        }
    }

    public async Task<TranscriptionResponse> CreateTranscriptionAsync(byte[] audio, string filename, TranscriptionOptions options, bool verbose, CancellationToken cancellationToken)
    {
        HttpContent BuildForm()
        {
            var form = new MultipartFormDataContent("MeetcoBoundary-" + Guid.NewGuid().ToString("N"));
            var file = new ByteArrayContent(audio);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", filename);
            form.Add(new StringContent(options.Model), "model");
            if (ModelCatalog.UsesLegacyTranscriptionParameters(options.Model))
            {
                form.Add(new StringContent(verbose ? "verbose_json" : "json"), "response_format");
                form.Add(new StringContent("0"), "temperature");
                if (options.LanguageCode is { } language) form.Add(new StringContent(language), "language");
            }
            else if (options.LanguageCode is { } language)
            {
                form.Add(new StringContent(language), "languages[]");
            }
            if (!string.IsNullOrEmpty(options.Prompt)) form.Add(new StringContent(options.Prompt), "prompt");
            return form;
        }

        using var response = await SendWithRetryAsync(
            () => MakeRequest("audio/transcriptions", HttpMethod.Post, BuildForm(), null),
            HttpCompletionOption.ResponseContentRead,
            retry: true,
            cancellationToken).ConfigureAwait(false);
        return await DecodeAsync<TranscriptionResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(string? apiKeyOverride, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(
            () => MakeRequest("models", HttpMethod.Get, null, apiKeyOverride),
            HttpCompletionOption.ResponseContentRead,
            retry: false,
            cancellationToken).ConfigureAwait(false);
        var list = await DecodeAsync<ModelListResponse>(response, cancellationToken).ConfigureAwait(false);
        return list.Data.Select(m => m.Id).Order(StringComparer.Ordinal).ToList();
    }

    // MARK: Transport

    private HttpRequestMessage MakeRequest(string path, HttpMethod method, HttpContent? content, string? apiKeyOverride)
    {
        var apiKey = apiKeyOverride ?? _keyProvider.CurrentKey();
        if (string.IsNullOrEmpty(apiKey)) throw OpenAIException.MissingApiKey();
        var configuration = _configuration();
        var request = new HttpRequestMessage(method, configuration.Endpoint(path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Options.Set(TimeoutKey, configuration.RequestTimeout);
        return request;
    }

    private static readonly HttpRequestOptionsKey<TimeSpan> TimeoutKey = new("meetco.timeout");

    /// <summary>Sends a request (rebuilt for every attempt) and returns a successful response.</summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> makeRequest,
        HttpCompletionOption completion,
        bool retry,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = makeRequest();
            var path = request.RequestUri?.AbsolutePath ?? "";
            try
            {
                return await SendOnceAsync(request, completion, cancellationToken).ConfigureAwait(false);
            }
            catch (OpenAIException error) when (retry && error.IsRetryable && attempt < _retryPolicy.MaxRetries)
            {
                attempt += 1;
                var backoff = Math.Min(_retryPolicy.MaxDelaySeconds, _retryPolicy.BaseDelaySeconds * Math.Pow(2, attempt - 1));
                var delay = error.RetryAfter is { } retryAfter
                    ? Math.Min(retryAfter.TotalSeconds, _retryPolicy.MaxDelaySeconds)
                    : backoff * (0.8 + Random.Shared.NextDouble() * 0.4);
                Log.Notice("network", $"Retrying {path} (attempt {attempt}) in {delay:0.0}s");
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var timeout = request.Options.TryGetValue(TimeoutKey, out var configured) ? configured : TimeSpan.FromSeconds(60);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, completion, timeoutSource.Token).ConfigureAwait(false);
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
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                body = "";
            }
            throw HttpError(response, body, request.RequestUri?.AbsolutePath ?? "");
        }
    }

    internal static OpenAIException HttpError(HttpResponseMessage response, string body, string path)
    {
        string? message = null;
        string? code = null;
        try
        {
            var envelope = JsonSerializer.Deserialize<OpenAIErrorEnvelope>(body, Json.Options);
            message = envelope?.Error?.Message;
            if (envelope?.Error?.Code is { ValueKind: JsonValueKind.String } codeElement) code = codeElement.GetString();
        }
        catch (JsonException)
        {
        }
        var status = (int)response.StatusCode;
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        Log.Error("network", $"HTTP {status} from {path}");
        return OpenAIException.Http(status, message ?? response.ReasonPhrase ?? $"HTTP {status}", code, retryAfter);
    }

    private static async Task<T> DecodeAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, Json.Options, cancellationToken).ConfigureAwait(false)
                ?? throw OpenAIException.Decoding("empty body");
        }
        catch (JsonException error)
        {
            throw OpenAIException.Decoding(error.Message);
        }
    }
}
