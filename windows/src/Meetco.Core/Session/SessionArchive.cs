using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;

namespace Meetco.Core.Session;

/// <summary>A finished listening session, stored locally only when the user enables it.</summary>
public sealed record ArchivedSession(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string? MeetingTopic,
    IReadOnlyList<TranscriptSegment> Segments,
    IReadOnlyList<Suggestion> Suggestions)
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

/// <summary>Saves transcripts as JSON in a local folder (%APPDATA%\Meetco\Sessions). Audio is never written to disk.</summary>
public sealed class SessionArchive(string directory)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Directory { get; } = directory;

    public void Save(ArchivedSession session)
    {
        if (session.Segments.Count == 0) return;
        System.IO.Directory.CreateDirectory(Directory);
        var name = "session-" + session.StartedAt.ToString("yyyy-MM-dd'T'HH-mm-ss", CultureInfo.InvariantCulture) + ".json";
        var path = Path.Combine(Directory, name);
        File.WriteAllText(path, JsonSerializer.Serialize(session, Options));
        Log.Info("storage", $"Saved session transcript ({session.Segments.Count} segments)");
    }

    public int SessionCount() =>
        System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory, "*.json").Length : 0;

    public void ClearAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return;
        foreach (var file in System.IO.Directory.GetFiles(Directory)) File.Delete(file);
        Log.Info("storage", "Cleared local session history");
    }
}
