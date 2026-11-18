using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meetco.Core.OpenAI;

namespace Meetco.Core.Transcription;

public enum LiveTranscriptionFailure { Unavailable, TimedOut, Failed }

public sealed class LiveTranscriptionException(LiveTranscriptionFailure failure, string? detail = null)
    : Exception(detail ?? failure.ToString())
{
    public LiveTranscriptionFailure Failure { get; } = failure;
}

/// <summary>Converts 16 kHz audio to the 24 kHz the Realtime API expects (linear interpolation).</summary>
public sealed class LinearUpsampler
{
    private const double Step = 16_000.0 / 24_000.0;
    private float _previous;
    /// <summary>Position of the next output sample, in input-sample units of the next batch.</summary>
    private double _position;

    public float[] Process(ReadOnlySpan<float> input)
    {
        if (input.IsEmpty) return [];
        var output = new List<float>(input.Length * 3 / 2 + 2);
        double end = input.Length - 1;
        while (_position <= end)
        {
            var index = (int)Math.Floor(_position);
            var fraction = (float)(_position - index);
            var left = index < 0 ? _previous : input[index];
            var right = index + 1 < input.Length ? input[index + 1] : left;
            output.Add(left + (right - left) * fraction);
            _position += Step;
        }
        _position -= input.Length;
        _previous = input[^1];
        return output.ToArray();
    }
}

/// <summary>Wire format of a Realtime API transcription session.</summary>
public static class RealtimeWire
{
    public abstract record ServerEvent
    {
        private ServerEvent() { }
        public sealed record Committed(string ItemId) : ServerEvent;
        public sealed record Delta(string ItemId, string Text) : ServerEvent;
        public sealed record Completed(string ItemId, string Transcript) : ServerEvent;
        public sealed record Failed(string ItemId, string Message) : ServerEvent;
        public sealed record Error(string Message) : ServerEvent;
        public sealed record Other : ServerEvent;
    }

    public const string Commit = """{"type":"input_audio_buffer.commit"}""";
    public const string Clear = """{"type":"input_audio_buffer.clear"}""";

    /// <summary>https://api.openai.com/v1 → wss://api.openai.com/v1/realtime?intent=transcription</summary>
    public static Uri Url(Uri baseUrl)
    {
        var builder = new UriBuilder(new OpenAIConfiguration(baseUrl, TimeSpan.Zero).Endpoint("realtime"))
        {
            Scheme = baseUrl.Scheme == Uri.UriSchemeHttp ? "ws" : "wss",
            Query = "intent=transcription",
            Port = baseUrl.IsDefaultPort ? -1 : baseUrl.Port,
        };
        return builder.Uri;
    }

    public static string SessionUpdate(string model, string? languageCode)
    {
        var transcription = new JsonObject { ["model"] = model, ["delay"] = "low" };
        if (languageCode is not null) transcription["languages"] = new JsonArray(languageCode);
        var update = new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["type"] = "transcription",
                ["audio"] = new JsonObject
                {
                    ["input"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24_000 },
                        ["transcription"] = transcription,
                        // Meetco detects the end of speech itself and commits each utterance.
                        ["turn_detection"] = null,
                    },
                },
            },
        };
        return update.ToJsonString(Json.Options);
    }

    /// <summary>input_audio_buffer.append with 24 kHz mono samples encoded as base64 PCM16.</summary>
    public static string Append(ReadOnlySpan<float> samples)
    {
        var pcm = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            var value = (short)(Math.Clamp(samples[index], -1f, 1f) * 32_767);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * 2, 2), value);
        }
        return """{"type":"input_audio_buffer.append","audio":""" + "\"" + Convert.ToBase64String(pcm) + "\"}";
    }

    public static ServerEvent Parse(string text)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return new ServerEvent.Other();
        }
        if (json is null || StringValue(json["type"]) is not { } type) return new ServerEvent.Other();
        var itemId = StringValue(json["item_id"]);
        return type switch
        {
            "input_audio_buffer.committed" when itemId is not null => new ServerEvent.Committed(itemId),
            "conversation.item.input_audio_transcription.delta" when itemId is not null && StringValue(json["delta"]) is { } delta
                => new ServerEvent.Delta(itemId, delta),
            "conversation.item.input_audio_transcription.completed" when itemId is not null
                => new ServerEvent.Completed(itemId, StringValue(json["transcript"]) ?? ""),
            "conversation.item.input_audio_transcription.failed" when itemId is not null
                => new ServerEvent.Failed(itemId, ErrorMessage(json)),
            "error" => new ServerEvent.Error(ErrorMessage(json)),
            _ => new ServerEvent.Other(),
        };
    }

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static string ErrorMessage(JsonObject json) =>
        (json["error"] as JsonObject)?["message"] is { } message ? StringValue(message) ?? "Unknown error" : "Unknown error";
}
