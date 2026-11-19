using System.Text.Json.Serialization;
using Meetco.Core.OpenAI;

namespace Meetco.Core.Models;

/// <summary>
/// The service that writes answers, recaps and questions. Everything except Anthropic speaks the
/// OpenAI Chat Completions protocol, so it shares <see cref="OpenAIClient"/>. Same set as the macOS app.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantProvider>))]
public enum AssistantProvider { OpenAI, Anthropic, DeepSeek, Ollama, LMStudio, Custom }

public enum ChatWire { OpenAICompatible, Anthropic }

public enum StructuredOutput { JsonSchema, JsonObject, None }

public enum TokenLimitField { MaxCompletionTokens, MaxTokens }

/// <summary>What a chat endpoint accepts, so requests are right the first time instead of after a 400.</summary>
public sealed record ChatCapabilities(bool Images, StructuredOutput StructuredOutput, TokenLimitField TokenLimit, bool ReasoningEffort);

public static class AssistantProviderInfo
{
    public static ChatWire Wire(this AssistantProvider provider) =>
        provider == AssistantProvider.Anthropic ? ChatWire.Anthropic : ChatWire.OpenAICompatible;

    public static string Title(this AssistantProvider provider) => provider switch
    {
        AssistantProvider.OpenAI => "OpenAI",
        AssistantProvider.Anthropic => "Anthropic (Claude)",
        AssistantProvider.DeepSeek => "DeepSeek",
        AssistantProvider.Ollama => "Ollama (local)",
        AssistantProvider.LMStudio => "LM Studio (local)",
        _ => "Custom (OpenAI-compatible)",
    };

    /// <summary>Short name for messages ("Add your DeepSeek API key…").</summary>
    public static string ShortName(this AssistantProvider provider) => provider switch
    {
        AssistantProvider.OpenAI => "OpenAI",
        AssistantProvider.Anthropic => "Anthropic",
        AssistantProvider.DeepSeek => "DeepSeek",
        AssistantProvider.Ollama => "Ollama",
        AssistantProvider.LMStudio => "LM Studio",
        _ => "custom provider",
    };

    public static Uri DefaultBaseUrl(this AssistantProvider provider) => provider switch
    {
        AssistantProvider.OpenAI => OpenAIConfiguration.DefaultBaseUrl,
        AssistantProvider.Anthropic => new Uri("https://api.anthropic.com/v1"),
        AssistantProvider.DeepSeek => new Uri("https://api.deepseek.com/v1"),
        AssistantProvider.Ollama => new Uri("http://localhost:11434/v1"),
        AssistantProvider.LMStudio => new Uri("http://localhost:1234/v1"),
        _ => new Uri("http://localhost:8080/v1"),
    };

    /// <summary>Local servers and many self-hosted gateways need no key.</summary>
    public static bool RequiresApiKey(this AssistantProvider provider) =>
        provider is AssistantProvider.OpenAI or AssistantProvider.Anthropic or AssistantProvider.DeepSeek;

    public static bool IsLocal(this AssistantProvider provider) =>
        provider is AssistantProvider.Ollama or AssistantProvider.LMStudio;

    public static string? KeyPageUrl(this AssistantProvider provider) => provider switch
    {
        AssistantProvider.OpenAI => "https://platform.openai.com/api-keys",
        AssistantProvider.Anthropic => "https://console.anthropic.com/settings/keys",
        AssistantProvider.DeepSeek => "https://platform.deepseek.com/api_keys",
        AssistantProvider.Ollama => "https://ollama.com/download",
        AssistantProvider.LMStudio => "https://lmstudio.ai",
        _ => null,
    };

    /// <summary>Model used until the user picks one. Empty for local servers: the list comes from the server.</summary>
    public static string DefaultModel(this AssistantProvider provider) => provider switch
    {
        AssistantProvider.OpenAI => ModelCatalog.DefaultAssistantModel,
        AssistantProvider.Anthropic => "claude-haiku-5-5",
        AssistantProvider.DeepSeek => "deepseek-chat",
        _ => "",
    };

    public static IReadOnlyList<ModelCatalog.Entry> ModelPresets(this AssistantProvider provider) => provider switch
    {
        AssistantProvider.OpenAI => ModelCatalog.AssistantModels,
        AssistantProvider.Anthropic =>
        [
            new("claude-haiku-5-5", "Fastest, great for live answers (recommended)"),
            new("claude-sonnet-5-5", "Smarter, slightly slower"),
            new("claude-opus-5-5", "Most capable, slowest to start"),
        ],
        AssistantProvider.DeepSeek =>
        [
            new("deepseek-chat", "Fast and inexpensive (recommended)"),
            new("deepseek-reasoner", "Thinks first: much slower to start"),
        ],
        _ => [],
    };

    public static ChatCapabilities Capabilities(this AssistantProvider provider) => provider switch
    {
        AssistantProvider.OpenAI => new(true, StructuredOutput.JsonSchema, TokenLimitField.MaxCompletionTokens, true),
        AssistantProvider.Anthropic => new(true, StructuredOutput.None, TokenLimitField.MaxTokens, false),
        // DeepSeek has no image input and only supports json_object.
        AssistantProvider.DeepSeek => new(false, StructuredOutput.JsonObject, TokenLimitField.MaxTokens, false),
        // Depends on the loaded model; unsupported features are dropped on the first 400.
        _ => new(true, StructuredOutput.JsonSchema, TokenLimitField.MaxTokens, false),
    };

    /// <summary>Windows Credential Manager target. OpenAI keeps the original target so existing keys still work.</summary>
    public static string CredentialTarget(this AssistantProvider provider) =>
        provider == AssistantProvider.OpenAI ? "Meetco/OpenAI-API-Key" : $"Meetco/{provider}-API-Key";
}
