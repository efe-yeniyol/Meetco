using System.Text.Json.Nodes;
using Meetco.Core.Assistant;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;

namespace Meetco.Core.Tests;

public class ResponseParserTests
{
    [Fact]
    public void ParsesAnswer()
    {
        const string json = """
            {"question_detected": true, "question": "Can your platform support 30 concurrent users?", "directed_at_user": true,
             "language": "English", "suggested_answer": "Yes, it can be configured for 30 concurrent users.",
             "explanation": "Evet, 30 eşzamanlı kullanıcı için yapılandırılabilir.", "confidence": 0.94,
             "needs_more_info": false, "meeting_topic": "Platform capacity"}
            """;
        var output = ResponseParser.Parse(json, SuggestionKind.Answer);
        Assert.True(output.QuestionDetected);
        Assert.Equal("English", output.Language);
        Assert.Equal(0.94, output.Confidence!.Value, 3);
        Assert.Equal("Platform capacity", output.MeetingTopic);
        Assert.NotNull(output.Explanation);
    }

    [Fact]
    public void AcceptsTurkishExplanationAliasAndCodeFence()
    {
        const string content = """
            ```json
            {"question_detected": true, "question": "Q", "language": "English", "suggested_answer": "A",
             "turkish_explanation": "Açıklama", "confidence": "0.5"}
            ```
            """;
        var output = ResponseParser.Parse(content, SuggestionKind.Answer);
        Assert.Equal("Açıklama", output.Explanation);
        Assert.Equal(0.5, output.Confidence);
    }

    [Fact]
    public void NoQuestionDetectedIsValid()
    {
        var output = ResponseParser.Parse("""{"question_detected": false, "suggested_answer": ""}""", SuggestionKind.Answer);
        Assert.False(output.QuestionDetected);
        Assert.Empty(output.Text);
    }

    [Fact]
    public void MissingAnswerThrows() =>
        Assert.Throws<AssistantException>(() => ResponseParser.Parse("""{"question_detected": true}""", SuggestionKind.Answer));

    [Fact]
    public void InvalidJsonThrows()
    {
        Assert.Throws<AssistantException>(() => ResponseParser.Parse("Sure! Here's a recap.", SuggestionKind.Recap));
        // Spoken answers are plain text, so prose is a valid answer.
        Assert.Equal("Sure, I can do that.", ResponseParser.Parse("Sure, I can do that.", SuggestionKind.Answer).Text);
        Assert.Throws<AssistantException>(() => ResponseParser.Parse("", SuggestionKind.Answer));
    }

    [Fact]
    public void ParsesRecap()
    {
        const string json = """{"summary": "Discussed pricing.", "key_points": ["Pricing", "Timeline"], "action_items": ["Send quote"], "open_questions": [], "language": "English", "meeting_topic": "Sales"}""";
        var output = ResponseParser.Parse(json, SuggestionKind.Recap);
        Assert.Equal(new[] { "Pricing", "Timeline" }, output.Items.Select(i => i.Text));
        Assert.Equal(new[] { "Send quote" }, output.ActionItems);
    }

    [Fact]
    public void ParsesQuestionsToAsk()
    {
        const string json = """{"questions": [{"question": "What is your timeline?", "explanation": "Zaman planınız nedir?"}, "Who else is involved?"], "language": "English", "meeting_topic": ""}""";
        var output = ResponseParser.Parse(json, SuggestionKind.QuestionsToAsk);
        Assert.Equal(2, output.Items.Count);
        Assert.Equal("Zaman planınız nedir?", output.Items[0].Detail);
    }

    [Fact]
    public void PlainAnswerWithExplanation()
    {
        var output = ResponseParser.Parse("\"Yes, we support SSO.\"\n===\nEvet, SSO destekliyoruz.", SuggestionKind.Answer);
        Assert.Equal("Yes, we support SSO.", output.Text);
        Assert.Equal("Evet, SSO destekliyoruz.", output.Explanation);
        Assert.False(ResponseParser.Parse("NONE.", SuggestionKind.Answer).QuestionDetected);
        Assert.Throws<AssistantException>(() => ResponseParser.Parse("NONE", SuggestionKind.WhatToSay));
    }

    [Fact]
    public void DecoratedNoneMeansNoQuestion()
    {
        Assert.False(ResponseParser.Parse("**NONE**", SuggestionKind.Answer).QuestionDetected);
        Assert.False(ResponseParser.Parse("(NONE)", SuggestionKind.Answer).QuestionDetected);
    }

    [Fact]
    public void DuplicateJsonKeysKeepTheLastValue()
    {
        var output = ResponseParser.Parse("""{"summary": "first", "summary": "second", "key_points": []}""", SuggestionKind.Recap);
        Assert.Equal("second", output.Text);
    }

    [Fact]
    public void PartialAnswerWaitsForPossibleNone()
    {
        Assert.Equal("", PlainAnswer.Partial("NO").Answer);
        Assert.Equal(false, PlainAnswer.Partial("NONE").QuestionDetected);
        var partial = PlainAnswer.Partial("Yes, I can ==");
        Assert.Equal("Yes, I can", partial.Answer);
        Assert.Equal(true, partial.QuestionDetected);
    }
}

public class PromptAndAssistantTests
{
    private static AssistantRequest Request(SuggestionKind kind = SuggestionKind.Answer, string model = "gpt-4.1-mini") => new()
    {
        Kind = kind,
        FocusQuestion = "Can your platform support 30 concurrent users?",
        Transcript = [new TranscriptLine(AudioSourceKind.System, "Can your platform support 30 concurrent users?", "en", DateTimeOffset.Now)],
        Preferences = new AssistantPreferences(ResponseLanguage.Auto, ExplanationLanguage.Turkish, ResponseStyle.Concise, "I work at Acme."),
        Model = model,
    };

    [Fact]
    public void PromptContainsTranscriptAndRules()
    {
        var request = Request();
        Assert.Equal(2, PromptBuilder.Messages(request).Count);
        var system = PromptBuilder.SystemPrompt(request);
        Assert.Contains("Never invent facts", system);
        Assert.Contains("Turkish", system);
        Assert.Contains("plain text only", system);
        var user = PromptBuilder.UserPrompt(request);
        Assert.Contains("Participant [en]: Can your platform support 30 concurrent users?", user);
        Assert.Contains("I work at Acme.", user);
    }

    [Fact]
    public void SchemaIsStrictAndOrdered()
    {
        var schema = PromptBuilder.Schema(SuggestionKind.Answer);
        var properties = Assert.IsType<JsonObject>(schema["properties"]);
        var required = Assert.IsType<JsonArray>(schema["required"]);
        Assert.Equal(properties.Select(p => p.Key), required.Select(r => r!.GetValue<string>()));
        Assert.Equal("question_detected", properties.First().Key);
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void RequestBodyKeepsSchemaOrderAndReadableText()
    {
        var body = new ChatCompletionRequest
        {
            Model = "gpt-4.1",
            Messages = [ChatMessage.User("Merhaba, nasılsınız?")],
            ResponseFormat = ResponseFormat.JsonSchema("meeting_recap", PromptBuilder.Schema(SuggestionKind.Recap)),
            Temperature = 0.5,
        }.ToJsonString();
        Assert.Contains("nasılsınız", body);
        Assert.True(body.IndexOf("\"summary\"", StringComparison.Ordinal) < body.IndexOf("\"key_points\"", StringComparison.Ordinal));
        Assert.Contains("\"strict\":true", body);
        Assert.DoesNotContain("reasoning_effort", body);
    }

    [Fact]
    public void ScreenshotIsAttachedAsImagePart()
    {
        var request = Request() with { ScreenContext = new ScreenContext([1, 2, 3], DateTimeOffset.Now, 10, 10) };
        var messages = PromptBuilder.Messages(request);
        Assert.True(messages[1].HasImage);
        Assert.Contains("data:image/jpeg;base64,AQID", new ChatCompletionRequest { Model = "m", Messages = messages }.ToJsonString());
    }

    [Fact]
    public async Task StreamsPlainAnswerAndReportsPartials()
    {
        var client = new FakeOpenAIClient("Yes, we can support 30 users.\n===\nEvet, 30 kullanıcıyı destekleyebiliriz.");
        var partials = new List<AssistantPartial>();
        var output = await new OpenAIMeetingAssistant(client).GenerateAsync(Request(), partials.Add, CancellationToken.None);
        Assert.Equal("Yes, we can support 30 users.", output.Text);
        Assert.Equal("Evet, 30 kullanıcıyı destekleyebiliriz.", output.Explanation);
        Assert.True(partials.Count > 2);
        Assert.StartsWith("Yes", partials[0].Answer);
        Assert.Null(client.Requests[0].ResponseFormat);
        Assert.Equal(0.5, client.Requests[0].Temperature);
    }

    [Fact]
    public async Task ReasoningModelsOmitTemperature()
    {
        var client = new FakeOpenAIClient("Sure.");
        await new OpenAIMeetingAssistant(client).GenerateAsync(Request(model: "gpt-6.1-terra"), _ => { }, CancellationToken.None);
        Assert.Null(client.Requests[0].Temperature);
        Assert.Equal("none", client.Requests[0].ReasoningEffort);
    }

    [Fact]
    public async Task RetriesWithoutRejectedParameter()
    {
        var client = new FakeOpenAIClient(
            OpenAIException.Http(400, "Unsupported value: 'reasoning_effort' does not support 'none' with this model."),
            "Sure.");
        var output = await new OpenAIMeetingAssistant(client).GenerateAsync(Request(model: "gpt-6.1-terra"), _ => { }, CancellationToken.None);
        Assert.Equal("Sure.", output.Text);
        Assert.Equal("low", client.Requests[1].ReasoningEffort);
    }

    [Fact]
    public async Task RecapUsesStructuredOutput()
    {
        var client = new FakeOpenAIClient("""{"summary": "We talked pricing.", "key_points": ["Price"], "action_items": [], "open_questions": [], "language": "English", "meeting_topic": "Pricing"}""");
        var output = await new OpenAIMeetingAssistant(client).GenerateAsync(Request(SuggestionKind.Recap), _ => { }, CancellationToken.None);
        Assert.Equal("We talked pricing.", output.Text);
        Assert.Equal("json_schema", client.Requests[0].ResponseFormat?.Type);
    }

    [Fact]
    public async Task TruncatedJsonIsReported()
    {
        var client = new FakeOpenAIClient("""{"summary": "We talked""");
        var error = await Assert.ThrowsAsync<AssistantException>(() =>
            new OpenAIMeetingAssistant(client).GenerateAsync(Request(SuggestionKind.Recap), _ => { }, CancellationToken.None));
        Assert.Equal(AssistantErrorKind.Truncated, error.Kind);
    }
}
