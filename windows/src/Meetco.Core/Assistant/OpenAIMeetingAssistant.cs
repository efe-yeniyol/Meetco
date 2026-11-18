using System.Text;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;

namespace Meetco.Core.Assistant;

/// <summary>Generates meeting suggestions with OpenAI Chat Completions.</summary>
public sealed class OpenAIMeetingAssistant(IOpenAIClient client) : IMeetingAssistant
{
    public async Task<AssistantOutput> GenerateAsync(AssistantRequest request, Action<AssistantPartial> onPartial, CancellationToken cancellationToken)
    {
        var isReasoning = ModelCatalog.IsReasoningModel(request.Model);
        // Spoken answers are plain text so the first token is already the answer (no schema
        // to compile, no JSON fields to write first). Recaps and question lists stay structured.
        var isSpoken = request.Kind.IsSpoken();
        var chat = new ChatCompletionRequest
        {
            Model = request.Model,
            Messages = PromptBuilder.Messages(request),
            ResponseFormat = isSpoken ? null : ResponseFormat.JsonSchema(PromptBuilder.SchemaName(request.Kind), PromptBuilder.Schema(request.Kind)),
            Temperature = isReasoning ? null : 0.5,
            MaxCompletionTokens = isReasoning ? 4000 : (request.Kind == SuggestionKind.Recap ? 900 : 600),
            ReasoningEffort = ModelCatalog.FastestReasoningEffort(request.Model),
        };

        // Adapt once or twice if a model rejects an optional parameter.
        var adjustments = 0;
        while (true)
        {
            try
            {
                return await StreamAsync(chat, request.Kind, onPartial, cancellationToken);
            }
            catch (OpenAIException error) when (error.Kind == OpenAIErrorKind.Http && error.Status == 400 && adjustments < 3)
            {
                adjustments += 1;
                var lower = error.Detail.ToLowerInvariant();
                if (lower.Contains("reasoning_effort") && chat.ReasoningEffort is not null)
                {
                    // The model does not know this effort level: fall back to "low", then to its default.
                    chat = chat with { ReasoningEffort = chat.ReasoningEffort == "low" ? null : "low" };
                }
                else if (lower.Contains("temperature") && chat.Temperature is not null)
                {
                    chat = chat with { Temperature = null };
                }
                else if ((lower.Contains("response_format") || lower.Contains("json_schema")) && chat.ResponseFormat?.Type == "json_schema")
                {
                    chat = chat with { ResponseFormat = ResponseFormat.JsonObjectFormat };
                }
                else if (lower.Contains("image") && chat.Messages.Any(m => m.HasImage))
                {
                    chat = chat with { Messages = PromptBuilder.Messages(request with { ScreenContext = null }) };
                }
                else
                {
                    throw;
                }
                Log.Notice("assistant", "Retrying chat request with adjusted parameters");
            }
        }
    }

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
