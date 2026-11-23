using System.Text;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;

namespace Meetco.Core.Assistant;

/// <summary>
/// Generates meeting suggestions with any OpenAI-compatible Chat Completions endpoint (OpenAI,
/// DeepSeek, Ollama, LM Studio, gateways). Requests follow <c>request.Provider</c>'s capabilities.
/// </summary>
public sealed class OpenAIMeetingAssistant(IOpenAIClient client) : IMeetingAssistant
{
    public async Task<AssistantOutput> GenerateAsync(AssistantRequest request, Action<AssistantPartial> onPartial, CancellationToken cancellationToken)
    {
        var chat = ChatRequest(request);

        // Adapt if a model rejects an optional parameter (local servers vary by model).
        var adjustments = 0;
        while (true)
        {
            try
            {
                return await StreamAsync(chat, request.Kind, onPartial, cancellationToken);
            }
            catch (OpenAIException error) when (error.Kind == OpenAIErrorKind.Http && error.Status is 400 or 422 && adjustments < 4)
            {
                adjustments += 1;
                var lower = error.Detail.ToLowerInvariant();
                var hasImage = chat.Messages.Any(m => m.HasImage);
                if (lower.Contains("reasoning_effort") && chat.ReasoningEffort is not null)
                {
                    // The model does not know this effort level: fall back to "low", then to its default.
                    chat = chat with { ReasoningEffort = chat.ReasoningEffort == "low" ? null : "low" };
                }
                else if (lower.Contains("temperature") && chat.Temperature is not null)
                {
                    chat = chat with { Temperature = null };
                }
                else if (lower.Contains("max_completion_tokens") && chat.MaxCompletionTokens is not null)
                {
                    chat = chat with { MaxTokens = chat.MaxCompletionTokens, MaxCompletionTokens = null };
                }
                else if (lower.Contains("max_tokens") && chat.MaxTokens is not null)
                {
                    chat = chat with { MaxCompletionTokens = chat.MaxTokens, MaxTokens = null };
                }
                else if ((lower.Contains("response_format") || lower.Contains("json_schema") || lower.Contains("json_object")) && chat.ResponseFormat is not null)
                {
                    // Strict schema → JSON mode → schema described in the prompt only.
                    chat = chat with
                    {
                        ResponseFormat = chat.ResponseFormat.Type == "json_schema" ? ResponseFormat.JsonObjectFormat : null,
                        Messages = PromptBuilder.Messages(Trimmed(request, keepImage: hasImage), inlineSchema: true),
                    };
                }
                else if (lower.Contains("image") && hasImage)
                {
                    var inline = chat.ResponseFormat?.Type != "json_schema";
                    chat = chat with { Messages = PromptBuilder.Messages(Trimmed(request, keepImage: false), inlineSchema: inline) };
                }
                else
                {
                    throw;
                }
                Log.Notice("assistant", "Retrying chat request with adjusted parameters");
            }
        }
    }

    /// <summary>The first request, shaped by what the provider is known to accept.</summary>
    public static ChatCompletionRequest ChatRequest(AssistantRequest request)
    {
        var capabilities = request.Provider.Capabilities();
        var isReasoning = capabilities.ReasoningEffort && ModelCatalog.IsReasoningModel(request.Model);
        // Spoken answers are plain text so the first token is already the answer (no schema
        // to compile, no JSON fields to write first). Recaps and question lists stay structured.
        var isSpoken = request.Kind.IsSpoken();
        var responseFormat = isSpoken ? null : capabilities.StructuredOutput switch
        {
            StructuredOutput.JsonSchema => ResponseFormat.JsonSchema(PromptBuilder.SchemaName(request.Kind), PromptBuilder.Schema(request.Kind)),
            StructuredOutput.JsonObject => ResponseFormat.JsonObjectFormat,
            _ => null,
        };
        var tokenLimit = isReasoning ? 4000 : request.Kind switch { SuggestionKind.Memory => 1500, SuggestionKind.Recap => 900, _ => 600 };
        return new ChatCompletionRequest
        {
            Model = request.Model,
            Messages = PromptBuilder.Messages(
                Trimmed(request, keepImage: capabilities.Images),
                inlineSchema: !isSpoken && capabilities.StructuredOutput != StructuredOutput.JsonSchema),
            ResponseFormat = responseFormat,
            Temperature = isReasoning ? null : 0.5,
            MaxCompletionTokens = capabilities.TokenLimit == TokenLimitField.MaxCompletionTokens ? tokenLimit : null,
            MaxTokens = capabilities.TokenLimit == TokenLimitField.MaxTokens ? tokenLimit : null,
            ReasoningEffort = isReasoning ? ModelCatalog.FastestReasoningEffort(request.Model) : null,
        };
    }

    private static AssistantRequest Trimmed(AssistantRequest request, bool keepImage) =>
        keepImage ? request : request with { ScreenContext = null };

    /// <summary>Streams the completion, emitting partial answers so the UI can show words as they arrive.</summary>
    private async Task<AssistantOutput> StreamAsync(ChatCompletionRequest chat, SuggestionKind kind, Action<AssistantPartial> onPartial, CancellationToken cancellationToken)
    {
        var streamsAnswer = kind.IsSpoken();
        var content = new StringBuilder();
        AssistantPartial lastPartial = new();
        await foreach (var delta in client.StreamChatCompletionAsync(chat, cancellationToken).WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
