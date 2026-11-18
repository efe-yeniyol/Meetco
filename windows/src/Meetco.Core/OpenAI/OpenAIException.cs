namespace Meetco.Core.OpenAI;

public enum OpenAIErrorKind { MissingApiKey, Http, Network, Decoding, EmptyResponse }

/// <summary>Errors from the OpenAI API, with user-facing messages.</summary>
public sealed class OpenAIException : Exception
{
    private OpenAIException(OpenAIErrorKind kind, string detail, int status = 0, string? code = null, TimeSpan? retryAfter = null)
        : base(detail)
    {
        Kind = kind;
        Detail = detail;
        Status = status;
        Code = code;
        RetryAfter = retryAfter;
    }

    public OpenAIErrorKind Kind { get; }
    public string Detail { get; }
    public int Status { get; }
    public string? Code { get; }
    public TimeSpan? RetryAfter { get; }

    public static OpenAIException MissingApiKey() => new(OpenAIErrorKind.MissingApiKey, "missing API key");
    public static OpenAIException Http(int status, string message, string? code = null, TimeSpan? retryAfter = null) =>
        new(OpenAIErrorKind.Http, message, status, code, retryAfter);
    public static OpenAIException Network(string message) => new(OpenAIErrorKind.Network, message);
    public static OpenAIException Decoding(string message) => new(OpenAIErrorKind.Decoding, message);
    public static OpenAIException EmptyResponse() => new(OpenAIErrorKind.EmptyResponse, "empty response");

    public bool IsRetryable => Kind switch
    {
        OpenAIErrorKind.Http => Code != "insufficient_quota" && (Status is 408 or 409 or 429 || Status is >= 500 and <= 599),
        OpenAIErrorKind.Network => true,
        _ => false,
    };

    public override string Message => Describe("OpenAI", "OpenAI");

    /// <summary>User-facing message naming the service that failed (every OpenAI-compatible provider raises these).</summary>
    public string Describe(string service, string settingsTab) => Kind switch
    {
        OpenAIErrorKind.MissingApiKey => $"No {service} API key is configured. Add your key in Settings → {settingsTab}.",
        OpenAIErrorKind.Http when Status == 401 => $"{service} rejected the API key (401). Check the key in Settings → {settingsTab}.",
        OpenAIErrorKind.Http when Code == "insufficient_quota" => $"Your {service} account has no available credit. Check billing in its dashboard.",
        OpenAIErrorKind.Http when Status == 402 => $"Your {service} account has no available credit (402). {Detail}",
        OpenAIErrorKind.Http when Status == 429 => $"{service} rate limit reached. Meetco will retry automatically. ({Detail})",
        OpenAIErrorKind.Http when Status == 404 => $"{service} could not find the requested model or endpoint (404). {Detail}",
        OpenAIErrorKind.Http => $"{service} error {Status}: {Detail}",
        OpenAIErrorKind.Network => $"Network error reaching {service}: {Detail}",
        OpenAIErrorKind.Decoding => $"Unexpected response from {service}: {Detail}",
        _ => $"{service} returned an empty response.",
    };
}
