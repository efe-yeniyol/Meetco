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

    public override string Message => Kind switch
    {
        OpenAIErrorKind.MissingApiKey => "No OpenAI API key is configured. Add your key in Settings → OpenAI.",
        OpenAIErrorKind.Http when Status == 401 => "OpenAI rejected the API key (401). Check the key in Settings → OpenAI.",
        OpenAIErrorKind.Http when Code == "insufficient_quota" => "Your OpenAI account has no available credit. Configure billing at platform.openai.com.",
        OpenAIErrorKind.Http when Status == 429 => $"OpenAI rate limit reached. Meetco will retry automatically. ({Detail})",
        OpenAIErrorKind.Http when Status == 404 => $"OpenAI could not find the requested model or endpoint (404). {Detail}",
        OpenAIErrorKind.Http => $"OpenAI error {Status}: {Detail}",
        OpenAIErrorKind.Network => $"Network error: {Detail}",
        OpenAIErrorKind.Decoding => $"Unexpected response from OpenAI: {Detail}",
        _ => "OpenAI returned an empty response.",
    };
}
