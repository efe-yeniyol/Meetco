using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Meetco.Core.OpenAI;

internal static class Json
{
    /// <summary>Compact JSON that keeps non-ASCII text readable (Turkish prompts) and object key order.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>One part of a multimodal user message.</summary>
public sealed record ContentPart(string Type, string? Text = null, string? ImageUrl = null, string Detail = "auto")
{
    public static ContentPart FromText(string text) => new("text", Text: text);

    public static ContentPart Jpeg(byte[] data, string detail = "auto") =>
        new("image_url", ImageUrl: "data:image/jpeg;base64," + Convert.ToBase64String(data), Detail: detail);

    internal JsonObject ToJson() => Type == "image_url"
        ? new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = ImageUrl, ["detail"] = Detail } }
        : new JsonObject { ["type"] = "text", ["text"] = Text };
}

public sealed record ChatMessage(string Role, string? Text, IReadOnlyList<ContentPart>? Parts = null)
{
    public static ChatMessage System(string text) => new("system", text);
    public static ChatMessage User(string text) => new("user", text);
    public static ChatMessage User(IReadOnlyList<ContentPart> parts) => new("user", null, parts);

    public bool HasImage => Parts?.Any(p => p.Type == "image_url") == true;

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["role"] = Role };
        if (Parts is not null) json["content"] = new JsonArray(Parts.Select(p => (JsonNode)p.ToJson()).ToArray());
        else json["content"] = Text ?? "";
        return json;
    }
}

public sealed record ResponseFormat(string Type, string? SchemaName = null, JsonObject? Schema = null)
{
    public static ResponseFormat JsonSchema(string name, JsonObject schema) => new("json_schema", name, schema);
    public static readonly ResponseFormat JsonObjectFormat = new("json_object");

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["type"] = Type };
        if (Type == "json_schema" && Schema is not null)
        {
            json["json_schema"] = new JsonObject
            {
                ["name"] = SchemaName,
                ["strict"] = true,
                ["schema"] = Schema.DeepClone(),
            };
        }
        return json;
    }
}

public sealed record ChatCompletionRequest
{
    public required string Model { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public ResponseFormat? ResponseFormat { get; init; }
    public double? Temperature { get; init; }
    public int? MaxCompletionTokens { get; init; }
    public string? ReasoningEffort { get; init; }
    public bool Stream { get; init; }
    /// <summary>Older name of max_completion_tokens, still the one most OpenAI-compatible servers know.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>JSON body; System.Text.Json.Nodes keeps the schema's declared key order.</summary>
    public string ToJsonString()
    {
        var json = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = new JsonArray(Messages.Select(m => (JsonNode)m.ToJson()).ToArray()),
        };
        if (ResponseFormat is not null) json["response_format"] = ResponseFormat.ToJson();
        if (Temperature is { } temperature) json["temperature"] = temperature;
        if (MaxCompletionTokens is { } maxCompletionTokens) json["max_completion_tokens"] = maxCompletionTokens;
        if (MaxTokens is { } maxTokens) json["max_tokens"] = maxTokens;
        if (ReasoningEffort is not null) json["reasoning_effort"] = ReasoningEffort;
        if (Stream) json["stream"] = true;
        return json.ToJsonString(Json.Options);
    }
}

/// <summary>One server-sent event of a streamed chat completion.</summary>
internal sealed class ChatCompletionChunk
{
    [JsonPropertyName("choices")] public List<ChunkChoice> Choices { get; set; } = [];

    internal sealed class ChunkChoice
    {
        [JsonPropertyName("delta")] public ChunkDelta? Delta { get; set; }
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    }

    internal sealed class ChunkDelta
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("refusal")] public string? Refusal { get; set; }
    }
}

public sealed class TranscriptionResponse
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    /// <summary>Only returned by whisper-1 with verbose_json (English language name, e.g. "turkish").</summary>
    [JsonPropertyName("language")] public string? Language { get; set; }
    /// <summary>Detected languages reported by newer transcription models (ISO codes), most likely first.</summary>
    [JsonPropertyName("languages")] public List<DetectedLanguage>? Languages { get; set; }

    public sealed class DetectedLanguage
    {
        [JsonPropertyName("code")] public string Code { get; set; } = "";
    }
}

internal sealed class ModelListResponse
{
    [JsonPropertyName("data")] public List<ModelEntry> Data { get; set; } = [];

    internal sealed class ModelEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
    }
}

internal sealed class OpenAIErrorEnvelope
{
    [JsonPropertyName("error")] public ErrorBody? Error { get; set; }

    internal sealed class ErrorBody
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("code")] public JsonElement? Code { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }
}

/// <summary>Options for a single transcription request.</summary>
public sealed record TranscriptionOptions(string Model, string? LanguageCode = null, string? Prompt = null,
    Models.TranscriptionProvider Provider = Models.TranscriptionProvider.OpenAI);

public sealed record TranscriptionResult(string Text, string? LanguageCode);
