using System.Text.Json;
using Meetco.Core.Assistant;
using Meetco.Core.Audio;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;
using Meetco.Core.Session;
using Meetco.Core.Settings;
using Meetco.Core.Text;
using Meetco.Core.Transcription;

namespace Meetco.Core.Tests;

public class MultiLanguageTests
{
    // MARK: Language catalog and settings

    [Fact]
    public void CatalogHasUniqueCodesAndKnownLanguages()
    {
        var codes = SpokenLanguage.All.Select(l => l.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.True(codes.Count >= 50);
        Assert.Equal("German", SpokenLanguage.ForCode("de")?.EnglishName);
        Assert.Equal("zh", SpokenLanguage.ForCode("zh-Hans")?.Code);
        Assert.Equal("no", SpokenLanguage.ForCode("nb")?.Code);
        Assert.Equal("Portuguese (Português)", SpokenLanguage.ForCode("pt-BR")?.Title);
        Assert.Equal("English", SpokenLanguage.ForCode("en")?.Title);
    }

    [Fact]
    public void LanguageChoicesParseOldAndNewValues()
    {
        // Older versions stored enum names.
        Assert.Equal(new ResponseLanguage("de"), LanguageChoice.Parse<ResponseLanguage>("German"));
        Assert.Equal(ResponseLanguage.Auto, LanguageChoice.Parse<ResponseLanguage>("Auto"));
        Assert.Equal(ExplanationLanguage.Turkish, LanguageChoice.Parse<ExplanationLanguage>("Turkish"));
        Assert.Equal(ExplanationLanguage.None, LanguageChoice.Parse<ExplanationLanguage>("None"));
        // New values are ISO codes.
        Assert.Equal("ja", LanguageChoice.Parse<TranscriptionLanguage>("ja")?.IsoCode());
        Assert.Equal("KO", new ExplanationLanguage("ko").Badge());
        Assert.Equal("French", new ResponseLanguage("fr").LanguageName());
        Assert.Null(LanguageChoice.Parse<ResponseLanguage>("klingon"));
        var all = LanguageChoice.AllChoices<ResponseLanguage>();
        Assert.Equal(SpokenLanguage.All.Count + 1, all.Count);
        Assert.Equal(ResponseLanguage.Auto, all[0]);
        Assert.Equal("Match the speaker (automatic)", all[0].Title());
    }

    [Fact]
    public void SettingsLoadOldFilesAndPersistAnyLanguage()
    {
        var path = Path.Combine(Path.GetTempPath(), "meetco-tests", Guid.NewGuid() + ".json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{ "responseLanguage": "Spanish", "explanationLanguage": "None", "transcriptionLanguage": "Turkish" }""");
            var settings = AppSettings.Load(path);
            Assert.Equal(new ResponseLanguage("es"), settings.ResponseLanguage);
            Assert.Equal(ExplanationLanguage.None, settings.ExplanationLanguage);
            Assert.Equal("tr", settings.TranscriptionLanguage.IsoCode());
            Assert.True(settings.DetectQuestionsInAnyLanguage, "on by default");

            settings.ExplanationLanguage = new ExplanationLanguage("ar");
            settings.TranscriptionLanguage = new TranscriptionLanguage("hi");
            settings.DetectQuestionsInAnyLanguage = false;
            Assert.Contains("\"explanationLanguage\": \"ar\"", File.ReadAllText(path));

            var reloaded = AppSettings.Load(path);
            Assert.Equal("Arabic", reloaded.ExplanationLanguage.LanguageName());
            Assert.Equal("hi", reloaded.TranscriptionLanguage.IsoCode());
            Assert.False(reloaded.DetectQuestionsInAnyLanguage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PromptNamesAnyLanguage()
    {
        var request = new AssistantRequest
        {
            Kind = SuggestionKind.Answer,
            Transcript = [],
            Preferences = new AssistantPreferences(new ResponseLanguage("ja"), new ExplanationLanguage("de"), ResponseStyle.Concise, ""),
            Model = "m",
        };
        var system = PromptBuilder.SystemPrompt(request);
        Assert.Contains("Always write suggested answers in Japanese.", system);
        Assert.Contains("German translation", system);
    }

    // MARK: Detection

    [Theory]
    [InlineData("你们的价格是多少？")]
    [InlineData("ما هو السعر؟")]
    [InlineData("¿Cuánto cuesta la licencia anual?")]
    [InlineData("Wie lange dauert die Integration?")]
    public void QuestionMarksInOtherScripts(string text) => Assert.True(new QuestionDetector().IsQuestion(text));

    [Fact]
    public void QuestionMarkEndings()
    {
        Assert.True(QuestionDetector.EndsWithQuestionMark("料金はいくらですか？」"));
        Assert.False(new QuestionDetector().IsQuestion("Wir planen den Start im nächsten Quartal."));
    }

    [Theory]
    [InlineData("Can you tell me more about the integration timeline for our team?", "en")]
    [InlineData("Entegrasyon süreci hakkında biraz daha bilgi verebilir misiniz?", "tr")]
    [InlineData("Können Sie mir mehr über die Integration mit unserem System erzählen", "de")]
    [InlineData("Pouvez-vous nous en dire plus sur le calendrier du projet", "fr")]
    [InlineData("¿Cómo funciona la integración con su plataforma?", "es")]
    [InlineData("プロジェクトのスケジュールについて教えてください", "ja")]
    [InlineData("你们的产品支持哪些语言", "zh")]
    [InlineData("프로젝트 일정에 대해 알려주세요", "ko")]
    [InlineData("Расскажите, пожалуйста, о сроках проекта", "ru")]
    [InlineData("Розкажіть, будь ласка, про терміни проєкту", "uk")]
    [InlineData("ما هو الجدول الزمني للمشروع", "ar")]
    [InlineData("Πώς λειτουργεί η ενσωμάτωση", "el")]
    [InlineData("परियोजना की समय सीमा क्या है", "hi")]
    public void DetectsManyLanguages(string text, string expected) => Assert.Equal(expected, LanguageDetector.Detect(text));

    [Fact]
    public void HallucinationsInOtherLanguages()
    {
        Assert.True(TranscriptTextFilter.IsLikelyHallucination("Sous-titres réalisés par la communauté d'Amara.org"));
        Assert.True(TranscriptTextFilter.IsLikelyHallucination("ご視聴ありがとうございました"));
        Assert.True(TranscriptTextFilter.IsLikelyHallucination("Продолжение следует..."));
        Assert.False(TranscriptTextFilter.IsLikelyHallucination("Merci pour la présentation détaillée."));
    }

    // MARK: Any-language question detection (probes)

    private sealed class Harness
    {
        public TranscriptStore Store { get; } = new();
        public AppSettings Settings { get; } = new();
        public ProbingAssistant Assistant { get; }
        public SuggestionEngine Engine { get; }

        public Harness(Func<AssistantRequest, string?> answer, TimeSpan delay = default)
        {
            Assistant = new ProbingAssistant(answer, delay);
            Engine = new SuggestionEngine(Assistant, null, Store, Settings, new ApiKeyRegistry(new ApiKeyProvider(new MemoryKeyStore("sk-test")), _ => new MemoryKeyStore()));
        }

        public TranscriptSegment Participant(string text, bool isQuestion = false, double afterSeconds = 0)
        {
            var start = DateTimeOffset.Now.AddSeconds(afterSeconds);
            var segment = new TranscriptSegment { Source = AudioSourceKind.System, Text = text, StartedAt = start, EndedAt = start.AddSeconds(1), IsQuestion = isQuestion };
            Store.Insert(segment);
            return segment;
        }
    }

    /// <summary>Answers with a partial first (like a streaming model), or "nothing to answer" when <c>answer</c> returns null.</summary>
    private sealed class ProbingAssistant(Func<AssistantRequest, string?> answer, TimeSpan delay) : IMeetingAssistant
    {
        public List<AssistantRequest> Requests { get; } = [];

        public async Task<AssistantOutput> GenerateAsync(AssistantRequest request, Action<AssistantPartial> onPartial, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await Task.Yield();
            if (answer(request) is not { } text) return new AssistantOutput { QuestionDetected = false };
            onPartial(new AssistantPartial { QuestionDetected = true, Answer = text });
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            return new AssistantOutput { Text = text };
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void ProbeAnswersAQuestionTheHeuristicMissed() => SingleThread.Run(async () =>
    {
        var harness = new Harness(_ => "Ich habe fünf Jahre Erfahrung.");
        var segment = harness.Participant("Erzählen Sie mir etwas über Ihre Erfahrung");
        harness.Engine.HandleNewSegment(segment, AudioCaptureMode.SystemAndMicrophone);
        Assert.False(harness.Engine.IsGenerating, "an unconfirmed probe shows no spinner");
        Assert.Null(harness.Engine.Draft);

        await WaitUntil(() => harness.Engine.Suggestions.Count == 1);
        Assert.Null(Assert.Single(harness.Assistant.Requests).FocusQuestion);
        Assert.Equal(segment.Text, harness.Engine.Suggestions[0].Question);
        Assert.Equal("Ich habe fünf Jahre Erfahrung.", harness.Engine.Suggestions[0].Text);
    });

    [Fact]
    public void ProbeDeclinedByModelLeavesNothing() => SingleThread.Run(async () =>
    {
        var harness = new Harness(_ => null);
        harness.Engine.HandleNewSegment(harness.Participant("Wir planen den Start im nächsten Quartal"), AudioCaptureMode.SystemAndMicrophone);
        await WaitUntil(() => harness.Assistant.Requests.Count == 1);
        await Task.Delay(50);
        Assert.Empty(harness.Engine.Suggestions);
        Assert.Null(harness.Engine.Draft);
        Assert.Null(harness.Engine.LastError);
    });

    [Fact]
    public void NoProbeWhenTurnedOff() => SingleThread.Run(async () =>
    {
        var harness = new Harness(_ => "Ja.");
        harness.Settings.DetectQuestionsInAnyLanguage = false;
        harness.Engine.HandleNewSegment(harness.Participant("Erzählen Sie mir etwas über Ihre Erfahrung"), AudioCaptureMode.SystemAndMicrophone);
        await Task.Delay(50);
        Assert.Empty(harness.Assistant.Requests);
    });

    [Fact]
    public void ProbeDoesNotInterruptAnAnswerBeingWritten() => SingleThread.Run(async () =>
    {
        var harness = new Harness(_ => "Our pricing is per seat.", TimeSpan.FromMilliseconds(300));
        harness.Engine.HandleNewSegment(harness.Participant("How does your pricing work?", isQuestion: true), AudioCaptureMode.SystemAndMicrophone);
        await WaitUntil(() => harness.Engine.Draft?.Text.Length > 0);

        harness.Engine.HandleNewSegment(harness.Participant("Wir haben ungefähr zweihundert Nutzer im Team", afterSeconds: 4), AudioCaptureMode.SystemAndMicrophone);
        await WaitUntil(() => harness.Engine.Suggestions.Count == 1);
        Assert.Single(harness.Assistant.Requests);
        Assert.Equal("Our pricing is per seat.", harness.Engine.Suggestions[0].Text);
    });
}
