using System.Text.Json.Serialization;
using Meetco.Core.Audio;

namespace Meetco.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ResponseLanguage>))]
public enum ResponseLanguage { Auto, English, Turkish, German, French, Spanish }

[JsonConverter(typeof(JsonStringEnumConverter<ExplanationLanguage>))]
public enum ExplanationLanguage { None, Turkish, English }

[JsonConverter(typeof(JsonStringEnumConverter<ResponseStyle>))]
public enum ResponseStyle { Concise, Detailed, Formal, Friendly, Technical }

[JsonConverter(typeof(JsonStringEnumConverter<TranscriptionLanguage>))]
public enum TranscriptionLanguage { Auto, English, Turkish }

/// <summary>Trade-off between transcription latency and API usage.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LatencyProfile>))]
public enum LatencyProfile { Fast, Balanced, Economical }

public static class PreferenceExtensions
{
    public static string Title(this ResponseLanguage language) =>
        language == ResponseLanguage.Auto ? "Match the speaker (automatic)" : language.LanguageName()!;

    /// <summary>English name of the language, used in prompts. Null for automatic.</summary>
    public static string? LanguageName(this ResponseLanguage language) => language switch
    {
        ResponseLanguage.English => "English",
        ResponseLanguage.Turkish => "Turkish",
        ResponseLanguage.German => "German",
        ResponseLanguage.French => "French",
        ResponseLanguage.Spanish => "Spanish",
        _ => null,
    };

    public static string Title(this ExplanationLanguage language) => language switch
    {
        ExplanationLanguage.Turkish => "Turkish",
        ExplanationLanguage.English => "English",
        _ => "Off",
    };

    public static string? LanguageName(this ExplanationLanguage language) => language switch
    {
        ExplanationLanguage.Turkish => "Turkish",
        ExplanationLanguage.English => "English",
        _ => null,
    };

    /// <summary>Short badge shown next to explanations.</summary>
    public static string Badge(this ExplanationLanguage language) => language switch
    {
        ExplanationLanguage.Turkish => "TR",
        ExplanationLanguage.English => "EN",
        _ => "",
    };

    public static string PromptDescription(this ResponseStyle style) => style switch
    {
        ResponseStyle.Concise => "Concise: no filler and no repetition, short sentences that are easy to say out loud. Usually 2–5 sentences; take as many as the substance needs (a recipe or an explanation needs its ingredients or steps), never fewer.",
        ResponseStyle.Detailed => "Detailed: 3–6 sentences that use relevant specifics from the conversation.",
        ResponseStyle.Formal => "Formal, polished business tone in 2–4 sentences.",
        ResponseStyle.Friendly => "Warm, friendly and conversational; usually 2–5 sentences, with the actual substance included.",
        _ => "Technically precise with correct terminology in 2–5 sentences.",
    };

    public static string Title(this TranscriptionLanguage language) => language switch
    {
        TranscriptionLanguage.English => "English",
        TranscriptionLanguage.Turkish => "Turkish",
        _ => "Detect automatically",
    };

    /// <summary>ISO-639-1 code passed to the transcription API, null for auto-detect.</summary>
    public static string? IsoCode(this TranscriptionLanguage language) => language switch
    {
        TranscriptionLanguage.English => "en",
        TranscriptionLanguage.Turkish => "tr",
        _ => null,
    };

    public static string Detail(this LatencyProfile profile) => profile switch
    {
        LatencyProfile.Fast => "Sends speech as soon as the speaker pauses. Best for live conversations. Recommended.",
        LatencyProfile.Balanced => "Waits for slightly longer pauses. Fewer API requests.",
        _ => "Sends longer passages. Fewer requests, higher latency.",
    };

    public static AudioChunkerConfiguration ChunkerConfiguration(this LatencyProfile profile) => profile switch
    {
        LatencyProfile.Fast => new AudioChunkerConfiguration { SilenceToFlush = 0.35, MinimumChunkDuration = 0.5, MaximumChunkDuration = 6 },
        LatencyProfile.Balanced => new AudioChunkerConfiguration { SilenceToFlush = 0.8, MinimumChunkDuration = 1.2, MaximumChunkDuration = 12 },
        _ => new AudioChunkerConfiguration { SilenceToFlush = 1.2, MinimumChunkDuration = 2.5, MaximumChunkDuration = 25 },
    };
}

/// <summary>Model presets offered in Settings. Users can also type any model id.</summary>
public static class ModelCatalog
{
    public sealed record Entry(string Id, string Note);

    public const string DefaultAssistantModel = "gpt-6.1-terra";
    public const string DefaultTranscriptionModel = "gpt-live-transcribe";

    public static readonly IReadOnlyList<Entry> AssistantModels =
    [
        new("gpt-6.1-terra", "Smart and fast, answers without thinking pauses (recommended)"),
        new("gpt-6-luna", "Fastest, lower quality"),
        new("gpt-6.1-sol", "Smarter, but always reasons first: slower to start"),
        new("gpt-6-astra", "Most capable, slowest to start"),
        new("gpt-4.1", "Previous generation"),
        new("gpt-4.1-mini", "Previous generation, fast"),
    ];

    public static readonly IReadOnlyList<Entry> TranscriptionModels =
    [
        new("gpt-live-transcribe", "Live streaming, lowest latency (recommended)"),
        new("gpt-transcribe", "Highest accuracy, uploads each phrase after it ends"),
        new("gpt-4o-mini-transcribe", "Deprecated, shuts down Feb 26, 2027"),
    ];

    /// <summary>Streaming models that hear the audio over a live connection while it is being spoken.</summary>
    public static bool IsLiveTranscriptionModel(string model) =>
        model.Contains("live-transcribe", StringComparison.OrdinalIgnoreCase);

    /// <summary>The model used for file uploads (live models cannot transcribe files).</summary>
    public static string FileTranscriptionModel(string model) =>
        IsLiveTranscriptionModel(model) ? "gpt-transcribe" : model;

    /// <summary>
    /// whisper-1 and the gpt-4o-*-transcribe family take language, temperature and response_format;
    /// newer models take a languages list and report detected languages.
    /// </summary>
    public static bool UsesLegacyTranscriptionParameters(string model)
    {
        var id = model.ToLowerInvariant();
        return id.StartsWith("whisper", StringComparison.Ordinal) || id.StartsWith("gpt-4o", StringComparison.Ordinal);
    }

    /// <summary>The lowest reasoning effort a model accepts: answers must start immediately.</summary>
    public static string? FastestReasoningEffort(string model)
    {
        if (!IsReasoningModel(model)) return null;
        var id = model.ToLowerInvariant();
        if (id.StartsWith("gpt-6-astra", StringComparison.Ordinal) || id.StartsWith("gpt-6.1-sol", StringComparison.Ordinal) || id.StartsWith('o')) return "low";
        // The first GPT-5 models (gpt-5, gpt-5-mini, gpt-5-nano) know "minimal" but not "none".
        if (id.StartsWith("gpt-5", StringComparison.Ordinal) && !id.StartsWith("gpt-5.", StringComparison.Ordinal)) return "minimal";
        return "none";
    }

    /// <summary>Reasoning models reject temperature and accept reasoning_effort.</summary>
    public static bool IsReasoningModel(string model)
    {
        var id = model.ToLowerInvariant();
        if (id.Contains("chat", StringComparison.Ordinal)) return false;
        return id.StartsWith("gpt-5", StringComparison.Ordinal) || id.StartsWith("gpt-6", StringComparison.Ordinal)
            || id.StartsWith("o1", StringComparison.Ordinal) || id.StartsWith("o3", StringComparison.Ordinal) || id.StartsWith("o4", StringComparison.Ordinal);
    }
}
