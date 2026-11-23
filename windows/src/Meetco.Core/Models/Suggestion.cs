using System.Text.Json.Serialization;

namespace Meetco.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<SuggestionKind>))]
public enum SuggestionKind
{
    Answer,
    WhatToSay,
    Recap,
    QuestionsToAsk,
    /// <summary>Background update of the meeting memory (people, facts, summary). Never shown as a card.</summary>
    Memory,
}

public static class SuggestionKindExtensions
{
    public static string Title(this SuggestionKind kind) => kind switch
    {
        SuggestionKind.Answer => "Suggested answer",
        SuggestionKind.WhatToSay => "What to say",
        SuggestionKind.Recap => "Recap so far",
        SuggestionKind.Memory => "Meeting memory",
        _ => "Questions to ask",
    };

    /// <summary>Answers that are spoken aloud are streamed as plain text.</summary>
    public static bool IsSpoken(this SuggestionKind kind) => kind is SuggestionKind.Answer or SuggestionKind.WhatToSay;
}

/// <summary>A secondary line inside a suggestion (recap key point, suggested question, ...).</summary>
public sealed record SuggestionItem(string Text, string? Detail = null)
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

/// <summary>A piece of AI output shown in the Answers tab.</summary>
public sealed record Suggestion
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required SuggestionKind Kind { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    /// <summary>The detected question this suggestion responds to.</summary>
    public string? Question { get; init; }
    /// <summary>Main text: suggested answer, or recap summary.</summary>
    public string Text { get; init; } = "";
    /// <summary>Optional explanation in the user's explanation language.</summary>
    public string? Explanation { get; init; }
    public string? LanguageName { get; init; }
    public double? Confidence { get; init; }
    public bool NeedsMoreInfo { get; init; }
    public bool DirectedAtUser { get; init; } = true;
    public IReadOnlyList<SuggestionItem> Items { get; init; } = [];
    public IReadOnlyList<string> ActionItems { get; init; } = [];
    public IReadOnlyList<string> OpenQuestions { get; init; } = [];
    public bool UsedScreenContext { get; init; }
    public Guid? TriggerSegmentId { get; init; }
    public bool IsAutomatic { get; init; }

    public string Title => Kind.Title();

    /// <summary>Short title for lists.</summary>
    public string PreviewTitle
    {
        get
        {
            if (!string.IsNullOrEmpty(Question)) return Question;
            if (!string.IsNullOrEmpty(Text)) return Text;
            return Items.Count > 0 ? Items[0].Text : Kind.Title();
        }
    }

    /// <summary>Plain text representation suitable for the clipboard.</summary>
    public string ClipboardText
    {
        get
        {
            switch (Kind)
            {
                case SuggestionKind.Answer:
                case SuggestionKind.WhatToSay:
                case SuggestionKind.Memory:
                    return Text;
                case SuggestionKind.Recap:
                    var parts = new List<string> { Text };
                    if (Items.Count > 0) parts.Add(string.Join("\n", Items.Select(i => "• " + i.Text)));
                    if (ActionItems.Count > 0) parts.Add("Action items:\n" + string.Join("\n", ActionItems.Select(a => "• " + a)));
                    if (OpenQuestions.Count > 0) parts.Add("Open questions:\n" + string.Join("\n", OpenQuestions.Select(q => "• " + q)));
                    return string.Join("\n\n", parts);
                default:
                    return string.Join("\n", Items.Select(i => i.Text));
            }
        }
    }
}
