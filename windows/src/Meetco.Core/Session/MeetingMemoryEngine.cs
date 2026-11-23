using Meetco.Core.Assistant;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;
using Meetco.Core.Settings;
using Meetco.Core.Transcription;

namespace Meetco.Core.Session;

/// <summary>
/// Keeps <see cref="MeetingMemory"/> up to date in the background while the user listens.
///
/// Every time enough new transcript has arrived (or some time has passed), the new lines and the
/// current memory go to the assistant model, which returns the merged memory: who is in the meeting
/// (names, roles, organizations) and what was said (facts, decisions, summary). Answer, recap and
/// question requests then include it, so answers stay consistent with the whole meeting rather than
/// only the last few minutes of transcript. Updates never block or cancel answers. UI-thread only.
/// </summary>
public sealed class MeetingMemoryEngine : ObservableObject
{
    /// <summary>New transcript characters that trigger an update.</summary>
    public const int UpdateThreshold = 350;
    /// <summary>Lower threshold while the memory is still empty, so introductions are picked up quickly.</summary>
    public const int FirstUpdateThreshold = 120;
    /// <summary>Any new transcript is processed after this long.</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(45);
    /// <summary>Wait after a failed update before trying again.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    /// <summary>At most this much new transcript per update (the oldest pending lines are dropped first).</summary>
    public const int MaxCharactersPerUpdate = 8_000;

    private readonly IMeetingAssistant _assistant;
    private readonly TranscriptStore _transcript;
    private readonly AppSettings _settings;
    private readonly Func<DateTimeOffset> _now;
    private readonly HashSet<Guid> _processed = [];
    private DateTimeOffset? _lastUpdate;
    private DateTimeOffset? _lastFailure;
    private CancellationTokenSource? _running;
    private Task? _task;
    private MeetingMemory _memory = new();
    private bool _isUpdating;

    public MeetingMemoryEngine(IMeetingAssistant assistant, TranscriptStore transcript, AppSettings settings, Func<DateTimeOffset>? now = null)
    {
        _assistant = assistant;
        _transcript = transcript;
        _settings = settings;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public MeetingMemory Memory
    {
        get => _memory;
        private set
        {
            if (!SetProperty(ref _memory, value)) return;
            OnPropertyChanged(nameof(Participants));
            OnPropertyChanged(nameof(RecentFacts));
            OnPropertyChanged(nameof(HasParticipants));
            OnPropertyChanged(nameof(HasFacts));
        }
    }

    public bool IsUpdating { get => _isUpdating; private set => SetProperty(ref _isUpdating, value); }

    public IReadOnlyList<MeetingParticipant> Participants => Memory.Participants;
    public bool HasParticipants => Memory.Participants.Count > 0;
    /// <summary>Newest facts first, for the sidebar.</summary>
    public IReadOnlyList<string> RecentFacts => Memory.Facts.Reverse().Take(8).ToList();
    public bool HasFacts => Memory.Facts.Count > 0;

    /// <summary>Receives the meeting topic the model reports.</summary>
    public Action<string>? OnTopic { get; set; }

    /// <summary>The memory to send with a request, or null when the feature is off or nothing is known yet.</summary>
    public MeetingMemory? Current => _settings.MeetingMemoryEnabled && !Memory.IsEmpty ? Memory : null;

    /// <summary>Call after each new transcript segment; starts an update when one is due.</summary>
    public void SegmentAdded()
    {
        if (!_settings.MeetingMemoryEnabled || _running is not null) return;
        var now = _now();
        if (_lastFailure is { } failure && now - failure < RetryDelay) return;
        var pending = _transcript.Segments.Where(s => !_processed.Contains(s.Id)).ToList();
        var characters = pending.Sum(s => s.Text.Length);
        if (characters == 0) return;
        var threshold = Memory.IsEmpty ? FirstUpdateThreshold : UpdateThreshold;
        var intervalPassed = _lastUpdate is { } last && now - last >= MaxInterval;
        if (characters < threshold && !intervalPassed) return;
        Update(pending);
    }

    public void Reset()
    {
        _running?.Cancel();
        _running = null;
        _task = null;
        IsUpdating = false;
        Memory = new MeetingMemory();
        _processed.Clear();
        _lastUpdate = null;
        _lastFailure = null;
    }

    /// <summary>Waits for the running update (tests).</summary>
    public Task WaitForUpdateAsync() => _task ?? Task.CompletedTask;

    private void Update(List<TranscriptSegment> pending)
    {
        var lines = new List<TranscriptLine>();
        var count = 0;
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            count += pending[i].Text.Length;
            if (count > MaxCharactersPerUpdate && lines.Count > 0) break;
            lines.Insert(0, TranscriptLine.From(pending[i]));
        }
        var request = new AssistantRequest
        {
            Kind = SuggestionKind.Memory,
            Transcript = lines,
            Preferences = _settings.AssistantPreferences,
            Model = _settings.AssistantModel,
            Provider = _settings.AssistantProvider,
            Memory = Memory,
        };
        var running = new CancellationTokenSource();
        _running = running;
        IsUpdating = true;
        Log.Info("assistant", $"Updating meeting memory ({lines.Count} new lines)");
        _task = RunAsync(request, pending.Select(s => s.Id).ToList(), running);
    }

    private async Task RunAsync(AssistantRequest request, List<Guid> ids, CancellationTokenSource running)
    {
        AssistantOutput? output = null;
        try
        {
            output = await _assistant.GenerateAsync(request, _ => { }, running.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Error("assistant", "Meeting memory update failed: " + error.Message);
        }
        // Reset while this update was running: drop its result.
        if (_running != running) return;
        _running = null;
        IsUpdating = false;
        if (output?.Memory is { } memory)
        {
            Memory = memory;
            _processed.UnionWith(ids);
            _lastUpdate = _now();
            _lastFailure = null;
            if (!string.IsNullOrEmpty(output.MeetingTopic)) OnTopic?.Invoke(output.MeetingTopic);
            // Lines that arrived during the update.
            SegmentAdded();
        }
        else
        {
            _lastFailure = _now();
        }
    }
}
