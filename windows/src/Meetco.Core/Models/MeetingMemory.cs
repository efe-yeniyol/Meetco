namespace Meetco.Core.Models;

/// <summary>A person in the meeting, as far as the conversation reveals them.</summary>
/// <param name="Name">The name as said in the meeting, or a short description when no name was said ("Customer's CTO").</param>
/// <param name="Notes">What they care about, asked or said that matters (one short line).</param>
public sealed record MeetingParticipant(string Name, string? Role = null, string? Organization = null, string? Notes = null)
{
    public string Id => Name.ToLowerInvariant();

    /// <summary>"Product manager, Acme", or null.</summary>
    public string? Detail
    {
        get
        {
            var parts = new[] { Role, Organization }.Where(p => !string.IsNullOrEmpty(p)).ToList();
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
    }
}

/// <summary>
/// What Meetco remembers about the current meeting beyond the recent transcript: who is there and
/// what has been said. Updated in the background by MeetingMemoryEngine and given to every request.
/// </summary>
public sealed record MeetingMemory
{
    public const int MaxParticipants = 15;
    public const int MaxFacts = 30;

    /// <summary>The user's own name, if it was said in the meeting.</summary>
    public string? UserName { get; init; }
    public IReadOnlyList<MeetingParticipant> Participants { get; init; } = [];
    /// <summary>Facts, numbers, decisions and commitments stated in the meeting, with who said them.</summary>
    public IReadOnlyList<string> Facts { get; init; } = [];
    /// <summary>Short summary of the conversation so far.</summary>
    public string Summary { get; init; } = "";

    public bool IsEmpty => UserName is null && Participants.Count == 0 && Facts.Count == 0 && Summary.Length == 0;

    /// <summary>Keeps the memory small enough to send with every answer request.</summary>
    public MeetingMemory Trimmed()
    {
        var seen = new HashSet<string>();
        return this with
        {
            Participants = Participants.Where(p => p.Name.Length > 0 && seen.Add(p.Id)).Take(MaxParticipants).ToList(),
            Facts = Facts.Where(f => f.Length > 0).TakeLast(MaxFacts).ToList(),
        };
    }
}
