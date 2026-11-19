namespace Meetco.Core.OpenAI;

/// <summary>Network configuration for the OpenAI API. Never contains credentials.</summary>
public sealed record OpenAIConfiguration(Uri BaseUrl, TimeSpan RequestTimeout)
{
    public static readonly Uri DefaultBaseUrl = new("https://api.openai.com/v1");

    public static OpenAIConfiguration From(string? rawBaseUrl) =>
        new(ValidatedBaseUrl(rawBaseUrl ?? "") ?? DefaultBaseUrl, TimeSpan.FromSeconds(60));

    /// <summary>Configuration of an assistant provider. Local models can take a while to load on the first request.</summary>
    public static OpenAIConfiguration For(Models.AssistantProvider provider, string? rawBaseUrl) =>
        new(ValidatedBaseUrl(rawBaseUrl ?? "") ?? Models.AssistantProviderInfo.DefaultBaseUrl(provider),
            TimeSpan.FromSeconds(Models.AssistantProviderInfo.IsLocal(provider) ? 120 : 60));

    public static Uri? ValidatedBaseUrl(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0 || !Uri.TryCreate(raw, UriKind.Absolute, out var url)) return null;
        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp) return null;
        return string.IsNullOrEmpty(url.Host) ? null : url;
    }

    /// <summary>Joins a relative API path onto the base URL (keeping the base path such as /v1).</summary>
    public Uri Endpoint(string path) => new(BaseUrl.AbsoluteUri.TrimEnd('/') + "/" + path.TrimStart('/'));
}
