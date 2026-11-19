using System.Globalization;
using System.Text.Json.Nodes;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;

namespace Meetco.Core.Assistant;

/// <summary>Builds the prompts and JSON schemas for every assistant request (same wording as the macOS app).</summary>
public static class PromptBuilder
{
    // MARK: Messages

    /// <param name="inlineSchema">Describe the JSON schema in the prompt, for endpoints without strict structured output.</param>
    public static List<ChatMessage> Messages(AssistantRequest request, bool inlineSchema = false)
    {
        var systemText = SystemPrompt(request);
        if (inlineSchema && SchemaInstructions(request.Kind) is { } schema) systemText += "\n" + schema;
        var system = ChatMessage.System(systemText);
        var userText = UserPrompt(request);
        if (request.ScreenContext is { } screen)
        {
            return
            [
                system,
                ChatMessage.User(
                [
                    ContentPart.FromText(userText),
                    ContentPart.FromText("A screenshot of the user's screen is attached (for example shared slides, a document or meeting chat). Use it only if it is relevant."),
                    ContentPart.Jpeg(screen.JpegData),
                ]),
            ];
        }
        return [system, ChatMessage.User(userText)];
    }

    public static string SystemPrompt(AssistantRequest request)
    {
        var preferences = request.Preferences;
        var shared = $"""
            You are Meetco, a real-time meeting copilot that sits beside the user during a live call.
            You read a rolling, automatically generated transcript and help the user respond.

            Transcript conventions:
            - "Participant" lines are other people in the meeting (captured from the computer's audio output).
            - "You" lines are the user's own microphone.
            - Speech recognition can contain mistakes; infer the intended meaning sensibly.

            Rules:
            - Use your own knowledge freely for anything that is general knowledge: how things work, definitions, recipes, techniques, technical concepts, best practices, well-known facts. Give the real content with concrete specifics (ingredients and typical amounts, steps, names, numbers that are commonly known).
            - Never invent facts about the user or their business: their personal history, employers, prices, dates, commitments, internal numbers or product capabilities must come from the transcript or the user's notes. Only when an answer truly depends on such private details, give an honest answer that offers to confirm or follow up.
            - Style: {preferences.Style.PromptDescription()}
            - Language: {LanguageRule(preferences.ResponseLanguage)}
            """;

        if (!request.Kind.IsSpoken())
        {
            return shared + "\n" + $"""
                - Explanation: {ExplanationRule(preferences.ExplanationLanguage)}
                - Always respond with a single JSON object that matches the requested schema.
                """;
        }

        return shared + "\n" + $"""
            - Do not repeat earlier suggestions verbatim; improve on them or add new substance.
            - Use the whole transcript to understand what is being talked about, so that short or vague questions ("and the second one?", "how long?") are answered in context.

            Substance — the answer must actually answer:
            - Answer the real request behind the words. "Do you know X?", "Have you heard of X?", "Can you explain X?", "Are you familiar with X?" are requests to deliver X itself: confirm in two or three words, then immediately give the content (for a recipe: the ingredients with amounts, then the steps; for a concept: the explanation; for a how-to: the steps in order; for an opinion: the opinion and the reason).
            - A reply that only says "Yes, I know that" or only restates the question is a failed answer. Someone who heard nothing but your answer should have learned the thing that was asked.
            - Put the most important information first so the user can already start speaking, then add the details.

            Voice — you are writing the user's own words:
            - Write exactly what the user will say, as the user, in the first person ("I", and "we" for their team or company). The user reads your text aloud word for word while you are still writing it.
            - Never talk to the user, never describe an answer ("You could say…", "A good answer would be…"), never mention being an AI, an assistant or Meetco.
            - Sound like a real person speaking in a meeting, not like written text: everyday words, contractions, short sentences that are easy to say in one breath. No bullet points, no markdown, no headings, no quotation marks around the answer.
            - No filler openers such as "Great question", "Certainly" or "Thank you for asking". The first words must already be the answer.
            - Match how the user talks in their own "You" lines (formality, vocabulary, level of detail) when there are any.
            - Treat the user's notes as the user's own background and experience and speak from them naturally ("In my last role I…").
            - When asked about the user's own work or experience, answer confidently and concretely with a clear structure (for example a brief situation → action → result), using only facts from the user's notes; otherwise give a credible, general answer without inventing specific employers, numbers or dates.

            Output format — plain text only, never JSON, no labels:
            - Start immediately with the words to say. Speed matters: the user is live on a call and waiting.
            {SpokenExplanationRule(preferences.ExplanationLanguage)}
            """;
    }

    public static string UserPrompt(AssistantRequest request)
    {
        var sections = new List<string>();
        var context = request.Preferences.UserContext.Trim();
        sections.Add("## About the user\n" + (context.Length == 0 ? "No additional information provided." : context));
        sections.Add("## Meeting topic so far\n" + (string.IsNullOrEmpty(request.MeetingTopic) ? "Not identified yet." : request.MeetingTopic));
        sections.Add("## Transcript (oldest first)\n" + FormattedTranscript(request.Transcript));
        if (request.PreviousAnswers.Count > 0)
        {
            sections.Add("## Earlier suggestions (avoid repeating)\n" + string.Join("\n", request.PreviousAnswers.Take(5).Select(a => "- " + a)));
        }
        sections.Add("## Task\n" + TaskText(request));
        return string.Join("\n\n", sections);
    }

    public static string FormattedTranscript(IReadOnlyList<TranscriptLine> lines)
    {
        if (lines.Count == 0) return "(The transcript is empty so far.)";
        return string.Join("\n", lines.Select(line =>
        {
            var language = line.LanguageCode is null ? "" : $" [{line.LanguageCode}]";
            var time = line.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            return $"[{time}] {line.Speaker.PromptLabel()}{language}: {line.Text}";
        }));
    }

    private static string TaskText(AssistantRequest request)
    {
        switch (request.Kind)
        {
            case SuggestionKind.Answer:
                if (request.FocusQuestion is { } question && request.FocusIsExplicit)
                {
                    return $"""
                        The user wants help answering this question from the meeting: "{question}"
                        Write the words the user should say in reply.
                        """;
                }
                var hint = request.FocusQuestion is { } focus ? $"\nThe most recent likely question is: \"{focus}\"" : "";
                return $"""
                    Identify the most recent meaningful question or request that a Participant addressed to the user (or to the user's side), focusing on the last few Participant lines.{hint}
                    If there is such a question, write the words the user should say in reply.
                    If there is nothing the user is expected to answer (small talk, rhetorical, already answered by the user, or a question between other participants), output exactly {PlainAnswer.NoQuestionMarker} and nothing else.
                    """;
            case SuggestionKind.WhatToSay:
                return """
                    Suggest what the user should say next given the current state of the conversation.
                    If a Participant question is still waiting for the user's answer, answer it.
                    Otherwise write a natural contribution, in the user's own voice, that moves the conversation forward.
                    """;
            case SuggestionKind.Recap:
                return $"""
                    Summarize the meeting so far for the user: a short summary, key points, action items (with owners when stated) and open questions.
                    Only include what was actually said. Write the recap in {RecapLanguage(request.Preferences)}.
                    """;
            default:
                return $"""
                    Suggest 3 to 5 smart, specific questions the user could ask the other participants next, based on the conversation so far.
                    Write them in the language the participants are speaking (or {request.Preferences.ResponseLanguage.LanguageName() ?? "the conversation language"} if set). Keep each under 25 words.
                    """;
        }
    }

    private static string LanguageRule(ResponseLanguage language) => language.LanguageName() is { } name
        ? $"Always write suggested answers in {name}."
        : "Write the suggested answer in the language the Participant used for the question. If the conversation switches languages, follow the most recent Participant language.";

    private static string ExplanationRule(ExplanationLanguage language) => language.LanguageName() is { } name
        ? $"If the suggested answer is not in {name}, write \"explanation\" in {name}: a short translation and explanation of the suggested answer so the user understands exactly what they will say. If the answer is already in {name}, set \"explanation\" to an empty string."
        : "Set \"explanation\" to an empty string.";

    /// <summary>Explanation instructions for the plain-text answer format.</summary>
    private static string SpokenExplanationRule(ExplanationLanguage language) => language.LanguageName() is { } name
        ? $"- If the answer is not in {name}: after the answer, write a line containing only {PlainAnswer.ExplanationDelimiter} and then a short {name} translation of the answer, so the user understands exactly what they are saying. If the answer is already in {name}, write nothing after it."
        : "- Write nothing after the answer: no explanation, no notes.";

    private static string RecapLanguage(AssistantPreferences preferences) =>
        preferences.ExplanationLanguage.LanguageName() ?? preferences.ResponseLanguage.LanguageName() ?? "the main language of the meeting";

    // MARK: Schemas

    /// <summary>The schema spelled out in the prompt. Null for spoken answers, which are plain text.</summary>
    public static string? SchemaInstructions(SuggestionKind kind) => kind.IsSpoken()
        ? null
        : "- Output only the JSON object: no markdown, no code fences, no text before or after it. Include every property, in this order, as described by this JSON Schema:\n"
          + Schema(kind).ToJsonString(Json.Options);

    public static string SchemaName(SuggestionKind kind) => kind switch
    {
        SuggestionKind.Recap => "meeting_recap",
        SuggestionKind.QuestionsToAsk => "meeting_questions",
        _ => "meeting_answer",
    };

    /// <summary>
    /// Field order matters: the model writes fields in schema order. JsonObject keeps insertion order.
    /// </summary>
    public static JsonObject Schema(SuggestionKind kind) => kind switch
    {
        SuggestionKind.Recap => StrictObject(
            ("summary", Type("string")),
            ("key_points", StringArray()),
            ("action_items", StringArray()),
            ("open_questions", StringArray()),
            ("language", Type("string")),
            ("meeting_topic", Type("string"))),
        SuggestionKind.QuestionsToAsk => StrictObject(
            ("questions", new JsonObject
            {
                ["type"] = "array",
                ["items"] = StrictObject(
                    ("question", Type("string")),
                    ("explanation", Type("string", "Explanation in the explanation language, or empty."))),
            }),
            ("language", Type("string")),
            ("meeting_topic", Type("string"))),
        _ => StrictObject(
            ("question_detected", Type("boolean", "Whether a question for the user was found.")),
            ("question", Type("string", "The question being answered, cleaned up. Empty if none.")),
            ("suggested_answer", Type("string", "What the user can say out loud.")),
            ("explanation", Type("string", "Explanation in the explanation language, or empty.")),
            ("language", Type("string", "Language of suggested_answer, e.g. English.")),
            ("directed_at_user", Type("boolean")),
            ("needs_more_info", Type("boolean", "True when facts the assistant cannot know are required.")),
            ("confidence", Type("number", "0 to 1.")),
            ("meeting_topic", Type("string", "Short description of the meeting topic."))),
    };

    private static JsonObject Type(string type, string? description = null)
    {
        var json = new JsonObject { ["type"] = type };
        if (description is not null) json["description"] = description;
        return json;
    }

    private static JsonObject StringArray() => new() { ["type"] = "array", ["items"] = Type("string") };

    /// <summary>Strict structured-output object: every property required, no extras.</summary>
    private static JsonObject StrictObject(params (string Key, JsonNode Value)[] properties)
    {
        var props = new JsonObject();
        foreach (var (key, value) in properties) props[key] = value;
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(properties.Select(p => (JsonNode)JsonValue.Create(p.Key)!).ToArray()),
            ["additionalProperties"] = false,
        };
    }
}
