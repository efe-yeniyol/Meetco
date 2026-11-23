using System.Text.Json.Nodes;
using Meetco.Core.Assistant;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;
using Meetco.Core.Session;
using Meetco.Core.Settings;
using Meetco.Core.Transcription;

namespace Meetco.Core.Tests;

public class MeetingMemoryTests
{
    private static readonly MeetingMemory Sample = new()
    {
        UserName = "Efe",
        Participants = [new MeetingParticipant("Ayşe", "Product manager", "Acme", "Cares about SSO")],
        Facts = ["Ayşe: budget is €50k for Q3", "Efe: rollout takes 6 weeks"],
        Summary = "Acme is evaluating the platform for 200 users.",
    };

    // MARK: Parsing and prompts

    [Fact]
    public void ParsesMemoryAndDropsDuplicates()
    {
        const string json = """
            {"user_name": "", "participants": [
                {"name": "Ayşe", "role": "Product manager", "organization": "Acme", "notes": ""},
                {"name": "ayşe", "role": "", "organization": "", "notes": ""},
                {"name": "Customer's CTO", "role": "CTO", "organization": "", "notes": "Asked about EU hosting"}
            ], "facts": ["Budget is €50k", ""], "summary": "Pricing call.", "meeting_topic": "Acme pricing"}
            """;
        var output = ResponseParser.Parse(json, SuggestionKind.Memory);
        var memory = Assert.IsType<MeetingMemory>(output.Memory);
        Assert.Null(memory.UserName);
        Assert.Equal(["Ayşe", "Customer's CTO"], memory.Participants.Select(p => p.Name));
        Assert.Equal("Product manager, Acme", memory.Participants[0].Detail);
        Assert.Null(memory.Participants[1].Organization);
        Assert.Equal(["Budget is €50k"], memory.Facts);
        Assert.Equal("Acme pricing", output.MeetingTopic);
    }

    [Fact]
    public void AnswerPromptUsesPeopleAndEarlierFacts()
    {
        var request = Request(SuggestionKind.Answer) with { Memory = Sample };
        var user = PromptBuilder.UserPrompt(request);
        Assert.Contains("## People in this meeting", user);
        Assert.Contains("- Efe (the user", user);
        Assert.Contains("- Ayşe — Product manager, Acme. Cares about SSO", user);
        Assert.Contains("- Ayşe: budget is €50k for Q3", user);
        Assert.Contains("Acme is evaluating", user);
        var system = PromptBuilder.SystemPrompt(request);
        Assert.Contains("ground truth for this meeting", system);
        Assert.Contains("Never guess a name or role", system);
        Assert.DoesNotContain("## People in this meeting", PromptBuilder.UserPrompt(Request(SuggestionKind.Answer)));
    }

    [Fact]
    public void MemoryRequestPromptAndSchema()
    {
        var request = Request(SuggestionKind.Memory) with { Memory = Sample };
        Assert.Equal(PromptBuilder.MemorySystemPrompt, PromptBuilder.SystemPrompt(request));
        Assert.Contains("Never guess names, roles or facts.", PromptBuilder.MemorySystemPrompt);
        var user = PromptBuilder.UserPrompt(request);
        Assert.Contains("## Existing memory\n{", user);
        Assert.Contains("\"participants\"", user);
        Assert.Contains("Ayşe", user);
        Assert.Contains("## New transcript lines", user);
        Assert.DoesNotContain("## People in this meeting", user);
        var schema = PromptBuilder.Schema(SuggestionKind.Memory);
        Assert.Equal(5, schema["required"]!.AsArray().Count);
        Assert.Equal("meeting_memory", PromptBuilder.SchemaName(SuggestionKind.Memory));
    }

    // MARK: Engine

    /// <summary>Returns a fixed memory for Memory requests and a short answer otherwise.</summary>
    private sealed class MemoryAssistant : IMeetingAssistant
    {
        public List<AssistantRequest> Requests { get; } = [];
        public bool Fails { get; set; }

        public async Task<AssistantOutput> GenerateAsync(AssistantRequest request, Action<AssistantPartial> onPartial, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await Task.Yield();
            if (Fails) throw new AssistantException(AssistantErrorKind.EmptyResponse);
            return request.Kind == SuggestionKind.Memory
                ? new AssistantOutput { Memory = new MeetingMemory { Participants = [new MeetingParticipant("Ayşe", Organization: "Acme")] }, MeetingTopic = "Acme rollout" }
                : new AssistantOutput { Text = "About six weeks." };
        }
    }

    private sealed class Harness
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Now;
        public TranscriptStore Store { get; } = new();
        public AppSettings Settings { get; } = new();
        public MemoryAssistant Assistant { get; } = new();
        public MeetingMemoryEngine Memory { get; }

        public Harness() => Memory = new MeetingMemoryEngine(Assistant, Store, Settings, () => Now);

        public TranscriptSegment Add(string text, bool isQuestion = false)
        {
            var segment = new TranscriptSegment { Source = AudioSourceKind.System, Text = text, StartedAt = Now, EndedAt = Now.AddSeconds(1), IsQuestion = isQuestion };
            Store.Insert(segment);
            return segment;
        }
    }

    [Fact]
    public void UpdatesAfterEnoughConversationAndSendsOnlyNewLines() => SingleThread.Run(async () =>
    {
        var harness = new Harness();
        harness.Add("Hi.");
        harness.Memory.SegmentAdded();
        Assert.Empty(harness.Assistant.Requests);

        harness.Add("I'm Ayşe, product manager at Acme. We have about two hundred users, a budget for the third quarter, and we need to decide on a tool before the end of the month.");
        harness.Memory.SegmentAdded();
        Assert.True(harness.Memory.IsUpdating);
        await harness.Memory.WaitForUpdateAsync();
        Assert.Equal("Ayşe", harness.Memory.Memory.Participants[0].Name);
        Assert.True(harness.Memory.HasParticipants);
        var first = Assert.Single(harness.Assistant.Requests);
        Assert.Equal(SuggestionKind.Memory, first.Kind);
        Assert.Equal(2, first.Transcript.Count);

        harness.Add(string.Concat(Enumerable.Repeat("We also need single sign-on and EU hosting for compliance. ", 7)));
        harness.Memory.SegmentAdded();
        await harness.Memory.WaitForUpdateAsync();
        Assert.Equal(2, harness.Assistant.Requests.Count);
        Assert.Single(harness.Assistant.Requests[1].Transcript);
        Assert.Equal("Ayşe", harness.Assistant.Requests[1].Memory?.Participants[0].Name);

        harness.Memory.Reset();
        Assert.True(harness.Memory.Memory.IsEmpty);
        Assert.Null(harness.Memory.Current);
    });

    [Fact]
    public void OffSendsNothingAndFailuresBackOff() => SingleThread.Run(async () =>
    {
        var harness = new Harness();
        harness.Settings.MeetingMemoryEnabled = false;
        harness.Add(string.Concat(Enumerable.Repeat("Long conversation. ", 30)));
        harness.Memory.SegmentAdded();
        Assert.Empty(harness.Assistant.Requests);

        harness.Settings.MeetingMemoryEnabled = true;
        harness.Assistant.Fails = true;
        harness.Memory.SegmentAdded();
        await harness.Memory.WaitForUpdateAsync();
        harness.Memory.SegmentAdded();
        Assert.Single(harness.Assistant.Requests);
        harness.Now += MeetingMemoryEngine.RetryDelay + TimeSpan.FromSeconds(1);
        harness.Memory.SegmentAdded();
        await harness.Memory.WaitForUpdateAsync();
        Assert.Equal(2, harness.Assistant.Requests.Count);
    });

    [Fact]
    public void AnswersIncludeTheMemory() => SingleThread.Run(async () =>
    {
        var harness = new Harness();
        var engine = new SuggestionEngine(harness.Assistant, null, harness.Store, harness.Settings,
            new ApiKeyRegistry(new ApiKeyProvider(new MemoryKeyStore("sk-test")), _ => new MemoryKeyStore()), memory: harness.Memory);

        harness.Add("Hello everyone, thanks for joining. I'm Ayşe, I lead the product team at Acme and we are evaluating tools.");
        var question = harness.Add("We have two hundred users. How long would the rollout take for us?", isQuestion: true);
        engine.HandleNewSegment(question, AudioCaptureMode.SystemAndMicrophone);
        await harness.Memory.WaitForUpdateAsync();
        Assert.Equal("Acme rollout", engine.MeetingTopic);

        engine.WhatShouldISay();
        await Task.Delay(50);
        var answer = harness.Assistant.Requests.Last(r => r.Kind == SuggestionKind.WhatToSay);
        Assert.Equal("Ayşe", answer.Memory?.Participants[0].Name);

        engine.Reset();
        Assert.True(harness.Memory.Memory.IsEmpty);
    });

    private static AssistantRequest Request(SuggestionKind kind) => new()
    {
        Kind = kind,
        Transcript = [new TranscriptLine(AudioSourceKind.System, "How long does it take?", "en", DateTimeOffset.Now)],
        Preferences = new AssistantPreferences(ResponseLanguage.Auto, ExplanationLanguage.None, ResponseStyle.Concise, ""),
        Model = "m",
    };
}
