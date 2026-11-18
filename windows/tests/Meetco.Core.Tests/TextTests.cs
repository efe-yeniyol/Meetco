using System.Buffers.Binary;
using System.Text;
using Meetco.Core.Assistant;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;
using Meetco.Core.Text;

namespace Meetco.Core.Tests;

public class QuestionDetectorTests
{
    private readonly QuestionDetector _detector = new();

    [Theory]
    [InlineData("Can your platform support 30 concurrent users?")]
    [InlineData("So how do you handle data retention for EU customers")]
    [InlineData("Could you walk us through the pricing")]
    [InlineData("Bu sistemi yarın kurabilir misiniz")]
    [InlineData("Fiyatlandırma aylık mı")]
    [InlineData("Entegrasyon ne kadar sürer")]
    [InlineData("Bunu nasıl yapıyorsunuz")]
    [InlineData("Tell me about how you handled the migration last quarter")]
    [InlineData("I'd like to hear about your experience with Swift")]
    [InlineData("Kendinizden biraz bahseder misiniz")]
    [InlineData("We discussed the roadmap. What is the timeline")]
    public void DetectsQuestions(string text) => Assert.True(_detector.IsQuestion(text), text);

    [Theory]
    [InlineData("We are planning to roll this out next quarter.")]
    [InlineData("I don't know what the timeline looks like.")]
    [InlineData("Toplantıyı yarın saat üçte yapalım.")]
    [InlineData("Okay")]
    public void IgnoresStatements(string text) => Assert.False(_detector.IsQuestion(text), text);
}

public class UtilityTests
{
    [Fact]
    public void WavHeader()
    {
        var data = WavEncoder.Encode(new float[] { 0, 0.5f, -0.5f, 1 }, 16_000, normalize: false);
        Assert.Equal(44 + 8, data.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(data, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(data, 8, 4));
        Assert.Equal(16_000u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24, 4)));
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(50, 2)));
    }

    [Fact]
    public void TextSimilarityComparesWords()
    {
        Assert.True(TextSimilarity.Jaccard("How much does it cost?", "how much does it cost") > 0.99);
        Assert.True(TextSimilarity.Jaccard("How much does it cost?", "When can we start the pilot?") < 0.2);
        Assert.Equal("nasil yapiyorsunuz", TextSimilarity.Normalized("Nasıl YAPIYORSUNUZ?"));
        Assert.Equal("cok guzel", TextSimilarity.Normalized("Çok  güzel!"));
    }

    [Fact]
    public void HallucinationFilter()
    {
        Assert.True(TranscriptTextFilter.IsLikelyHallucination("Thank you for watching!"));
        Assert.True(TranscriptTextFilter.IsLikelyHallucination("Altyazı M.K."));
        Assert.True(TranscriptTextFilter.IsLikelyHallucination("you you you you"));
        Assert.False(TranscriptTextFilter.IsLikelyHallucination("Thank you for the detailed overview of the roadmap."));
        Assert.Equal("Hello there team", TranscriptTextFilter.Clean("[Music] Hello there  team"));
    }

    [Fact]
    public void LanguageDetection()
    {
        Assert.Equal("en", LanguageDetector.Detect("Can you tell me more about the integration timeline for our team?"));
        Assert.Equal("tr", LanguageDetector.Detect("Entegrasyon süreci hakkında biraz daha bilgi verebilir misiniz?"));
        Assert.Null(LanguageDetector.Detect("ok"));
        Assert.Equal("tr", LanguageDetector.CodeForLanguageName("turkish"));
        Assert.Equal("en", LanguageDetector.CodeForLanguageName("English"));
    }

    [Fact]
    public void ReasoningModelDetection()
    {
        Assert.True(ModelCatalog.IsReasoningModel("gpt-5-mini"));
        Assert.True(ModelCatalog.IsReasoningModel("o4-mini"));
        Assert.False(ModelCatalog.IsReasoningModel("gpt-4.1-mini"));
        Assert.False(ModelCatalog.IsReasoningModel("gpt-5-chat-latest"));
        Assert.Equal("minimal", ModelCatalog.FastestReasoningEffort("gpt-5-mini"));
        Assert.Equal("none", ModelCatalog.FastestReasoningEffort("gpt-6.1-terra"));
        Assert.Equal("low", ModelCatalog.FastestReasoningEffort("gpt-6-astra"));
        Assert.Null(ModelCatalog.FastestReasoningEffort("gpt-4.1"));
        Assert.Equal("gpt-transcribe", ModelCatalog.FileTranscriptionModel("gpt-live-transcribe"));
    }

    [Fact]
    public void BaseUrlValidation()
    {
        Assert.NotNull(OpenAIConfiguration.ValidatedBaseUrl("https://api.openai.com/v1"));
        Assert.Null(OpenAIConfiguration.ValidatedBaseUrl("not a url"));
        Assert.Null(OpenAIConfiguration.ValidatedBaseUrl("ftp://example.com"));
        Assert.Equal("https://api.openai.com/v1/chat/completions",
            OpenAIConfiguration.From(null).Endpoint("chat/completions").AbsoluteUri);
        Assert.Equal("https://proxy.example.com/openai/v1/models",
            OpenAIConfiguration.From("https://proxy.example.com/openai/v1/").Endpoint("models").AbsoluteUri);
    }
}

public class PartialJsonTests
{
    [Fact]
    public void IncompleteString()
    {
        const string text = """{"question_detected": true, "question": "Why", "suggested_answer": "I think the main re""";
        var answer = PartialJson.String("suggested_answer", text);
        Assert.Equal("I think the main re", answer?.Value);
        Assert.False(answer?.IsComplete);
        Assert.Equal("Why", PartialJson.String("question", text)?.Value);
        Assert.True(PartialJson.Bool("question_detected", text));
    }

    [Fact]
    public void Escapes()
    {
        const string text = """{"suggested_answer": "Say \"yes\"\nthen çok ç"}""";
        var answer = PartialJson.String("suggested_answer", text);
        Assert.Equal("Say \"yes\"\nthen çok ç", answer?.Value);
        Assert.True(answer?.IsComplete);
    }

    [Fact]
    public void DanglingEscapeIsIncomplete()
    {
        const string text = """{"suggested_answer": "abc\""";
        Assert.Equal("abc", PartialJson.String("suggested_answer", text)?.Value);
    }

    [Fact]
    public void MissingKey()
    {
        Assert.Null(PartialJson.String("suggested_answer", """{"question": "x" """));
        Assert.Null(PartialJson.Bool("question_detected", """{"question_detected": """));
    }
}
