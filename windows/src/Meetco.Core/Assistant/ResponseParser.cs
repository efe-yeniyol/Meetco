using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meetco.Core.Models;

namespace Meetco.Core.Assistant;

/// <summary>Parses (and tolerates minor deviations in) the model's output.</summary>
public static class ResponseParser
{
    public static AssistantOutput Parse(string content, SuggestionKind kind)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new AssistantException(AssistantErrorKind.EmptyResponse);
        if (kind.IsSpoken() && !PlainAnswer.LooksLikeJson(content)) return ParsePlainAnswer(content, kind);

        var json = JsonObjectFrom(content);
        var meetingTopic = Str(json["meeting_topic"]);
        var language = Str(json["language"]);
        var explanation = Str(json["explanation"]) ?? Str(json["turkish_explanation"]);
        var output = new AssistantOutput { MeetingTopic = meetingTopic, Language = language, Explanation = explanation };

        switch (kind)
        {
            case SuggestionKind.Answer:
            case SuggestionKind.WhatToSay:
            {
                var text = Str(json["suggested_answer"]) ?? Str(json["answer"]) ?? "";
                var detected = Bool(json["question_detected"]) ?? text.Length > 0;
                if (text.Length == 0 && (kind == SuggestionKind.WhatToSay || detected))
                {
                    throw new AssistantException(AssistantErrorKind.InvalidResponse, "missing suggested_answer");
                }
                return output with
                {
                    Text = text,
                    QuestionDetected = detected,
                    Question = Str(json["question"]),
                    DirectedAtUser = Bool(json["directed_at_user"]) ?? true,
                    Confidence = Number(json["confidence"]) is { } confidence ? Math.Clamp(confidence, 0, 1) : null,
                    NeedsMoreInfo = Bool(json["needs_more_info"]) ?? false,
                };
            }
            case SuggestionKind.Recap:
            {
                var text = Str(json["summary"]) ?? "";
                var items = Strings(json["key_points"]).Select(s => new SuggestionItem(s)).ToList();
                if (text.Length == 0 && items.Count == 0) throw new AssistantException(AssistantErrorKind.InvalidResponse, "missing summary");
                return output with
                {
                    Text = text,
                    Items = items,
                    ActionItems = Strings(json["action_items"]),
                    OpenQuestions = Strings(json["open_questions"]),
                };
            }
            case SuggestionKind.Memory:
            {
                var people = new List<MeetingParticipant>();
                if (json["participants"] is JsonArray participants)
                {
                    foreach (var entry in participants)
                    {
                        if (entry is JsonObject person && Str(person["name"]) is { } name)
                        {
                            people.Add(new MeetingParticipant(name, Str(person["role"]), Str(person["organization"]), Str(person["notes"])));
                        }
                    }
                }
                var memory = new MeetingMemory
                {
                    UserName = Str(json["user_name"]),
                    Participants = people,
                    Facts = Strings(json["facts"]),
                    Summary = Str(json["summary"]) ?? "",
                };
                return output with { Memory = memory.Trimmed() };
            }
            default:
            {
                var items = new List<SuggestionItem>();
                if (json["questions"] is JsonArray questions)
                {
                    foreach (var entry in questions)
                    {
                        if (Str(entry) is { } plain) items.Add(new SuggestionItem(plain));
                        else if (entry is JsonObject obj && Str(obj["question"]) is { } question) items.Add(new SuggestionItem(question, Str(obj["explanation"])));
                    }
                }
                if (items.Count == 0) throw new AssistantException(AssistantErrorKind.InvalidResponse, "no questions");
                return output with { Items = items };
            }
        }
    }

    /// <summary>Parses the plain-text answer format (see <see cref="PlainAnswer"/>).</summary>
    public static AssistantOutput ParsePlainAnswer(string content, SuggestionKind kind)
    {
        var (answer, explanation) = PlainAnswer.Split(content);
        if (answer.Length == 0 || PlainAnswer.IsNoQuestion(answer))
        {
            if (kind == SuggestionKind.WhatToSay) throw new AssistantException(AssistantErrorKind.EmptyResponse);
            return new AssistantOutput { QuestionDetected = false };
        }
        return new AssistantOutput { Text = answer, Explanation = explanation };
    }

    /// <summary>Extracts a JSON object even if wrapped in code fences or surrounded by prose.</summary>
    public static JsonObject JsonObjectFrom(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.Length == 0) throw new AssistantException(AssistantErrorKind.EmptyResponse);
        if (Decode(trimmed) is { } direct) return direct;
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start && Decode(trimmed[start..(end + 1)]) is { } embedded) return embedded;
        throw new AssistantException(AssistantErrorKind.InvalidResponse, "invalid JSON");
    }

    private static JsonObject? Decode(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject json) return null;
            _ = json.Count; // Materializes the properties; throws on duplicate keys.
            return json;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return DecodeKeepingLastDuplicate(text);
        }
    }

    /// <summary>Models occasionally repeat a key; keep the last value, like JSONSerialization does.</summary>
    private static JsonObject? DecodeKeepingLastDuplicate(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var json = new JsonObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                json[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
            return json;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String) return null;
        var trimmed = value.GetValue<string>().Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(Str).OfType<string>().ToList() : [];

    private static bool? Bool(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        switch (value.GetValueKind())
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number: return value.GetValue<double>() != 0;
            case JsonValueKind.String:
                return value.GetValue<string>().ToLowerInvariant() is "true" or "yes" or "1";
            default: return null;
        }
    }

    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        return value.GetValueKind() switch
        {
            JsonValueKind.Number => value.GetValue<double>(),
            JsonValueKind.String when double.TryParse(value.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}
