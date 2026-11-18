using System.Collections.ObjectModel;
using System.Globalization;
using Meetco.Core.Assistant;
using Meetco.Core.Models;

namespace Meetco.Core.Transcription;

/// <summary>Chronological, in-memory transcript for the current session. UI-thread only.</summary>
public sealed class TranscriptStore
{
    public ObservableCollection<TranscriptSegment> Segments { get; } = [];

    /// <summary>Inserts a segment keeping chronological order (chunks can finish out of order).</summary>
    public void Insert(TranscriptSegment segment)
    {
        var index = 0;
        for (var i = Segments.Count - 1; i >= 0; i--)
        {
            if (Segments[i].StartedAt <= segment.StartedAt)
            {
                index = i + 1;
                break;
            }
        }
        Segments.Insert(index, segment);
    }

    public void Clear() => Segments.Clear();

    public TranscriptSegment? LastSegment(AudioSourceKind source) => Segments.LastOrDefault(s => s.Source == source);

    /// <summary>Most recent text from a source, used as a continuity prompt for transcription.</summary>
    public string RecentText(AudioSourceKind source, int maxCharacters)
    {
        var collected = new List<string>();
        var count = 0;
        for (var i = Segments.Count - 1; i >= 0; i--)
        {
            var segment = Segments[i];
            if (segment.Source != source) continue;
            collected.Insert(0, segment.Text);
            count += segment.Text.Length;
            if (count >= maxCharacters) break;
        }
        var joined = string.Join(' ', collected);
        return joined.Length > maxCharacters ? joined[^maxCharacters..] : joined;
    }

    /// <summary>The most recent segments that fit within a character budget, oldest first.</summary>
    public List<TranscriptLine> RecentLines(int maxCharacters)
    {
        var lines = new List<TranscriptLine>();
        var count = 0;
        for (var i = Segments.Count - 1; i >= 0; i--)
        {
            var segment = Segments[i];
            count += segment.Text.Length + 24;
            if (count > maxCharacters && lines.Count > 0) break;
            lines.Add(TranscriptLine.From(segment));
        }
        lines.Reverse();
        return lines;
    }

    public string PlainText => string.Join("\n", Segments.Select(s =>
        $"[{s.StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {s.SpeakerLabel}: {s.Text}"));
}
