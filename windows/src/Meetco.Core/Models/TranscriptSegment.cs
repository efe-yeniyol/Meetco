namespace Meetco.Core.Models;

/// <summary>One transcribed utterance from a single audio source.</summary>
public sealed record TranscriptSegment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required AudioSourceKind Source { get; init; }
    public required string Text { get; init; }
    /// <summary>ISO language code such as "en" or "tr" (detected, may be null).</summary>
    public string? LanguageCode { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    /// <summary>True when the local heuristic thinks the segment contains a question.</summary>
    public bool IsQuestion { get; init; }

    public string SpeakerLabel => Source.SpeakerLabel();
    public bool IsFromUser => Source == AudioSourceKind.Microphone;
}
