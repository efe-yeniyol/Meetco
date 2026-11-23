using System.Text;
using Meetco.Core.Anthropic;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;

namespace Meetco.Core.Assistant;

/// <summary>
/// Generates meeting suggestions with Claude through Anthropic's Messages API. Uses the same prompts
/// and parsing as the OpenAI path; there is no response_format, so structured kinds get their JSON
/// schema described in the prompt.
/// </summary>
public sealed class AnthropicMeetingAssistant(IAnthropicClient client) : IMeetingAssistant
{
    public async Task<AssistantOutput> GenerateAsync(AssistantRequest request, Action<AssistantPartial> onPartial, CancellationToken cancellationToken)
    {
        var message = MessageRequest(request);
        var adjustments = 0;
        while (true)
        {
            try
            {
                return await StreamAsync(message, request.Kind, onPartial, cancellationToken);
            }
            catch (OpenAIException error) when (error.Kind == OpenAIErrorKind.Http && error.Status == 400 && adjustments < 2)
            {
                adjustments += 1;
                var lower = error.Detail.ToLowerInvariant();
                if (lower.Contains("temperature") && message.Temperature is not null)
                {
                    message = message with { Temperature = null };
                }
                else if (lower.Contains("image") && request.ScreenContext is not null)
                {
                    message = MessageRequest(request with { ScreenContext = null }, message.Temperature);
                }
                else
                {
                    throw;
                }
                Log.Notice("assistant", "Retrying Anthropic request with adjusted parameters");
            }
        }
    }

    public static AnthropicMessageRequest MessageRequest(AssistantRequest request, double? temperature = 0.5) =>
        AnthropicMessageRequest.From(
            request.Model,
            PromptBuilder.Messages(request, inlineSchema: true),
            request.Kind == SuggestionKind.Memory ? 1500 : request.Kind == SuggestionKind.Recap ? 1200 : 800,
            temperature);

    private async Task<AssistantOutput> StreamAsync(AnthropicMessageRequest message, SuggestionKind kind, Action<AssistantPartial> onPartial, CancellationToken cancellationToken)
    {
        var streamsAnswer = kind.IsSpoken();
        var content = new StringBuilder();
        AssistantPartial lastPartial = new();
        await foreach (var delta in client.StreamMessageAsync(message, cancellationToken).WithCancellation(cancellationToken))
        {
            content.Append(delta);
            if (!streamsAnswer) continue;
            var partial = AssistantPartial.Extract(content.ToString());
            if (partial != lastPartial)
            {
                lastPartial = partial;
                onPartial(partial);
            }
        }
        var text = content.ToString();
        if (text.Length == 0) throw new AssistantException(AssistantErrorKind.EmptyResponse);
        try
        {
            return ResponseParser.Parse(text, kind);
        }
        catch (AssistantException) when (PlainAnswer.LooksLikeJson(text) && !text.TrimEnd().EndsWith('}'))
        {
            throw new AssistantException(AssistantErrorKind.Truncated);
        }
    }
}

/// <summary>Sends each request to the provider it names, and labels errors with that provider's name.</summary>
public sealed class ProviderRoutingAssistant(
    IReadOnlyDictionary<AssistantProvider, IOpenAIClient> chatClients,
    IAnthropicClient anthropic,
    Func<AssistantProvider, OpenAIConfiguration>? configuration = null) : IMeetingAssistant
{
    public async Task<AssistantOutput> GenerateAsync(AssistantRequest request, Action<AssistantPartial> onPartial, CancellationToken cancellationToken)
    {
        try
        {
            if (request.Provider.Wire() == ChatWire.Anthropic)
            {
                return await new AnthropicMeetingAssistant(anthropic).GenerateAsync(request, onPartial, cancellationToken);
            }
            if (!chatClients.TryGetValue(request.Provider, out var client)) throw OpenAIException.MissingApiKey();
            return await new OpenAIMeetingAssistant(client).GenerateAsync(request, onPartial, cancellationToken);
        }
        catch (OpenAIException error)
        {
            throw new ProviderException(request.Provider, error, configuration?.Invoke(request.Provider).BaseUrl);
        }
    }
}

/// <summary>An API error attributed to the assistant provider that produced it.</summary>
public sealed class ProviderException(AssistantProvider provider, OpenAIException underlying, Uri? baseUrl = null)
    : Exception(underlying.Detail, underlying)
{
    public AssistantProvider Provider { get; } = provider;
    public OpenAIException Underlying { get; } = underlying;

    public override string Message =>
        Provider.IsLocal() && Underlying.Kind == OpenAIErrorKind.Network
            ? $"Can't reach {Provider.ShortName()} at {baseUrl ?? Provider.DefaultBaseUrl()}. Make sure it is running and a model is loaded."
            : Underlying.Describe(Provider.ShortName(), "Assistant");
}
