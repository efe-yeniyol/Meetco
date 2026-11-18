using System.Collections.ObjectModel;
using Meetco.Core.Assistant;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;
using Meetco.Core.Settings;
using Meetco.Core.Text;
using Meetco.Core.Transcription;

namespace Meetco.Core.Session;

/// <summary>
/// Decides *when* to ask the model for help and manages the resulting suggestions.
///
/// Automatic flow: a Participant segment that looks like a question starts a short debounce
/// (so the full question is captured), duplicates are skipped, and any in-flight automatic
/// request is cancelled when a newer question arrives. Manual actions (What should I say?,
/// Recap, Questions to ask, Regenerate) are always honored. UI-thread only.
/// </summary>
public sealed class SuggestionEngine : ObservableObject
{
    /// <summary>Wait after a question-like segment, in case the participant keeps talking.</summary>
    public static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(0.25);
    /// <summary>
    /// A segment that ends with a question mark is answered right away; if the participant keeps
    /// talking, the answer restarts with the full question.
    /// </summary>
    public static readonly TimeSpan QuestionMarkDebounce = TimeSpan.Zero;
    public static readonly TimeSpan MaxDebounceWait = TimeSpan.FromSeconds(1.0);
    /// <summary>A follow-up phrase within this window is treated as the rest of the question.</summary>
    public static readonly TimeSpan ContinuationWindow = TimeSpan.FromSeconds(2.5);
    public const double DuplicateThreshold = 0.72;

    private sealed record AutomaticTrigger(Guid SegmentId, string QuestionText, DateTimeOffset QuestionEnd, DateTimeOffset FiredAt);

    private readonly IMeetingAssistant _assistant;
    private readonly IScreenContextCapture? _screenCapture;
    private readonly TranscriptStore _transcript;
    private readonly AppSettings _settings;
    private readonly ApiKeyProvider _keyProvider;
    private readonly Func<DateTimeOffset> _now;

    private CancellationTokenSource? _automatic;
    private readonly Dictionary<SuggestionKind, CancellationTokenSource> _manual = [];
    private CancellationTokenSource? _debounce;
    private TranscriptSegment? _pendingQuestion;
    private DateTimeOffset? _pendingSince;
    private readonly List<string> _recentAutomaticQuestions = [];
    private AutomaticTrigger? _lastAutomatic;
    private Guid? _draftRequestId;
    private readonly List<SuggestionKind> _activeKinds = [];

    private Guid? _selectedSuggestionId;
    private Suggestion? _draft;
    private string? _lastError;
    private string? _meetingTopic;
    private ScreenContext? _pendingScreenContext;
    private bool _isCapturingScreen;

    public SuggestionEngine(
        IMeetingAssistant assistant,
        IScreenContextCapture? screenCapture,
        TranscriptStore transcript,
        AppSettings settings,
        ApiKeyProvider keyProvider,
        Func<DateTimeOffset>? now = null)
    {
        _assistant = assistant;
        _screenCapture = screenCapture;
        _transcript = transcript;
        _settings = settings;
        _keyProvider = keyProvider;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    // MARK: Observable state

    /// <summary>Newest first.</summary>
    public ObservableCollection<Suggestion> Suggestions { get; } = [];

    /// <summary>The answer currently being streamed (shown live, word by word).</summary>
    public Suggestion? Draft
    {
        get => _draft;
        private set
        {
            if (SetProperty(ref _draft, value)) NotifyDisplay();
        }
    }

    public Guid? SelectedSuggestionId
    {
        get => _selectedSuggestionId;
        set
        {
            if (SetProperty(ref _selectedSuggestionId, value)) NotifyDisplay();
        }
    }

    public string? LastError { get => _lastError; set => SetProperty(ref _lastError, value); }
    public string? MeetingTopic { get => _meetingTopic; private set => SetProperty(ref _meetingTopic, value); }

    public ScreenContext? PendingScreenContext
    {
        get => _pendingScreenContext;
        set
        {
            if (SetProperty(ref _pendingScreenContext, value)) OnPropertyChanged(nameof(HasPendingScreenContext));
        }
    }

    public bool HasPendingScreenContext => PendingScreenContext is not null;
    public bool IsCapturingScreen { get => _isCapturingScreen; private set => SetProperty(ref _isCapturingScreen, value); }

    public bool IsGenerating => _activeKinds.Count > 0;
    public bool IsGeneratingKind(SuggestionKind kind) => _activeKinds.Contains(kind);
    public bool IsGeneratingRecap => IsGeneratingKind(SuggestionKind.Recap);
    public bool IsGeneratingWhatToSay => IsGeneratingKind(SuggestionKind.WhatToSay);
    public bool IsGeneratingQuestions => IsGeneratingKind(SuggestionKind.QuestionsToAsk);

    /// <summary>What the Answers view should show: the live draft, the selected suggestion, or the latest one.</summary>
    public Suggestion? CurrentSuggestion => Draft is not null && SelectedSuggestionId is null ? Draft : DisplayedSuggestion;

    public bool IsStreamingCurrent => Draft is not null && CurrentSuggestion?.Id == Draft.Id;

    public Suggestion? DisplayedSuggestion
    {
        get
        {
            if (SelectedSuggestionId is { } id && Suggestions.FirstOrDefault(s => s.Id == id) is { } selected) return selected;
            return Suggestions.FirstOrDefault();
        }
    }

    /// <summary>1-based position of the displayed suggestion in history (newest = 1).</summary>
    public (int Index, int Count)? HistoryPosition
    {
        get
        {
            if (DisplayedSuggestion is not { } current) return null;
            var index = IndexOf(current.Id);
            return index < 0 ? null : (index + 1, Suggestions.Count);
        }
    }

    public string HistoryLabel => HistoryPosition is { } position && position.Count > 1 ? $"{position.Index}/{position.Count}" : "";
    public bool CanShowOlder => HistoryPosition is { } position && position.Index < position.Count;
    public bool CanShowNewer => HistoryPosition is { } position && position.Index > 1;

    public void ShowOlder()
    {
        if (HistoryPosition is not { } position || position.Index >= position.Count) return;
        SelectedSuggestionId = Suggestions[position.Index].Id;
    }

    public void ShowNewer()
    {
        if (HistoryPosition is not { } position || position.Index <= 1) return;
        var newer = Suggestions[position.Index - 2];
        SelectedSuggestionId = position.Index - 2 == 0 ? null : newer.Id;
    }

    // MARK: Automatic triggering

    public void HandleNewSegment(TranscriptSegment segment, AudioCaptureMode captureMode)
    {
        if (!_settings.AutoSuggest || !_keyProvider.HasKey) return;
        // Questions come from other participants; in mic-only mode (in-person) everyone is on the mic.
        var isFromOthers = segment.Source == AudioSourceKind.System || captureMode == AudioCaptureMode.MicrophoneOnly;
        if (!isFromOthers) return;

        if (segment.IsQuestion)
        {
            if (_pendingQuestion is null) _pendingSince = _now();
            if (_pendingQuestion is { } pending)
            {
                // Merge consecutive question fragments into one question.
                _pendingQuestion = pending with
                {
                    Text = pending.Text + " " + segment.Text,
                    LanguageCode = segment.LanguageCode,
                    EndedAt = segment.EndedAt,
                    IsQuestion = true,
                };
            }
            else
            {
                _pendingQuestion = segment;
            }
            ScheduleAutomaticTrigger(segment.Text.TrimEnd().EndsWith('?'));
        }
        else if (_pendingQuestion is not null)
        {
            // The participant is still talking: wait a little longer for the full question.
            ScheduleAutomaticTrigger(false);
        }
        else if (_lastAutomatic is { } last
                 && segment.Text.Length >= 12
                 && segment.StartedAt - last.QuestionEnd < ContinuationWindow
                 && (_now() - last.FiredAt).TotalSeconds < 8)
        {
            // The question continued after we already started answering: answer the full question.
            ContinueAutomaticAnswer(last, segment);
        }
    }

    private void ContinueAutomaticAnswer(AutomaticTrigger last, TranscriptSegment segment)
    {
        var combined = last.QuestionText + " " + segment.Text;
        _lastAutomatic = new AutomaticTrigger(last.SegmentId, combined, segment.EndedAt, _now());
        var previous = Suggestions.FirstOrDefault(s => s.TriggerSegmentId == last.SegmentId);
        _automatic?.Cancel();
        _automatic = Generate(SuggestionKind.Answer, combined, false, last.SegmentId, automatic: true, replacing: previous);
    }

    private void ScheduleAutomaticTrigger(bool endsWithQuestionMark)
    {
        _debounce?.Cancel();
        var now = _now();
        var since = _pendingSince ?? now;
        var wait = endsWithQuestionMark ? QuestionMarkDebounce : DebounceInterval;
        var fireAt = Min(now + wait, since + MaxDebounceWait);
        var delay = fireAt - now;
        if (delay.TotalSeconds <= 0.01)
        {
            FireAutomaticTrigger();
            return;
        }
        var debounce = new CancellationTokenSource();
        _debounce = debounce;
        _ = DelayThenFireAsync(delay, debounce.Token);
    }

    private async Task DelayThenFireAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!token.IsCancellationRequested) FireAutomaticTrigger();
    }

    private void FireAutomaticTrigger()
    {
        if (_pendingQuestion is not { } question) return;
        _pendingQuestion = null;
        _pendingSince = null;

        if (_recentAutomaticQuestions.Any(previous => TextSimilarity.Jaccard(previous, question.Text) >= DuplicateThreshold))
        {
            Log.Debug("assistant", "Skipping duplicate question");
            return;
        }
        _recentAutomaticQuestions.Add(question.Text);
        if (_recentAutomaticQuestions.Count > 12) _recentAutomaticQuestions.RemoveAt(0);
        _lastAutomatic = new AutomaticTrigger(question.Id, question.Text, question.EndedAt, _now());

        _automatic?.Cancel();
        _automatic = Generate(SuggestionKind.Answer, question.Text, false, question.Id, automatic: true);
    }

    // MARK: Manual actions

    public void WhatShouldISay() => Manual(SuggestionKind.WhatToSay, () => Generate(SuggestionKind.WhatToSay));
    public void Recap() => Manual(SuggestionKind.Recap, () => Generate(SuggestionKind.Recap));
    public void QuestionsToAsk() => Manual(SuggestionKind.QuestionsToAsk, () => Generate(SuggestionKind.QuestionsToAsk));

    public void Answer(TranscriptSegment segment) =>
        Manual(SuggestionKind.Answer, () => Generate(SuggestionKind.Answer, segment.Text, true, segment.Id));

    public void Regenerate(Suggestion suggestion) =>
        Manual(suggestion.Kind, () => Generate(
            suggestion.Kind,
            suggestion.Question,
            suggestion.Question is not null,
            suggestion.TriggerSegmentId,
            replacing: suggestion));

    public void Dismiss(Suggestion suggestion)
    {
        var index = IndexOf(suggestion.Id);
        if (index >= 0) Suggestions.RemoveAt(index);
        if (SelectedSuggestionId == suggestion.Id) SelectedSuggestionId = null;
        NotifyDisplay();
    }

    public void Select(Suggestion suggestion) => SelectedSuggestionId = suggestion.Id;

    public async void CaptureScreenContext()
    {
        if (IsCapturingScreen || _screenCapture is null) return;
        IsCapturingScreen = true;
        try
        {
            PendingScreenContext = await _screenCapture.CaptureMainDisplayAsync();
        }
        catch (Exception error)
        {
            LastError = error.Message;
        }
        finally
        {
            IsCapturingScreen = false;
        }
    }

    /// <summary>Clears suggestions for a new session.</summary>
    public void Reset()
    {
        _debounce?.Cancel();
        _automatic?.Cancel();
        foreach (var source in _manual.Values) source.Cancel();
        _manual.Clear();
        _activeKinds.Clear();
        _pendingQuestion = null;
        _pendingSince = null;
        _recentAutomaticQuestions.Clear();
        _lastAutomatic = null;
        _draftRequestId = null;
        Draft = null;
        Suggestions.Clear();
        SelectedSuggestionId = null;
        MeetingTopic = null;
        LastError = null;
        NotifyGenerating();
        NotifyDisplay();
    }

    // MARK: Generation

    private void Manual(SuggestionKind kind, Func<CancellationTokenSource?> start)
    {
        if (_manual.Remove(kind, out var previous)) previous.Cancel();
        if (start() is { } source) _manual[kind] = source;
    }

    private CancellationTokenSource? Generate(
        SuggestionKind kind,
        string? focusQuestion = null,
        bool focusIsExplicit = false,
        Guid? triggerSegmentId = null,
        bool automatic = false,
        Suggestion? replacing = null)
    {
        if (!_keyProvider.HasKey)
        {
            LastError = OpenAIException.MissingApiKey().Message;
            return null;
        }

        var budget = kind == SuggestionKind.Recap ? 24_000 : 3_000;
        var request = new AssistantRequest
        {
            Kind = kind,
            FocusQuestion = focusQuestion,
            FocusIsExplicit = focusIsExplicit,
            Transcript = _transcript.RecentLines(budget),
            MeetingTopic = MeetingTopic,
            Preferences = _settings.AssistantPreferences,
            Model = _settings.ChatModel,
            ScreenContext = PendingScreenContext,
            PreviousAnswers = PreviousAnswers(replacing),
        };
        _activeKinds.Add(kind);
        NotifyGenerating();
        Log.Info("assistant", $"Requesting {kind} (automatic: {automatic})");

        var requestId = Guid.NewGuid();
        var draftId = replacing?.Id ?? Guid.NewGuid();
        if (kind.IsSpoken())
        {
            _draftRequestId = requestId;
            // Show the question the moment it is heard, before the first word of the answer arrives.
            if (!string.IsNullOrEmpty(focusQuestion))
            {
                Draft = new Suggestion
                {
                    Id = draftId,
                    Kind = kind,
                    Question = focusQuestion,
                    TriggerSegmentId = triggerSegmentId,
                    IsAutomatic = automatic,
                };
                SelectedSuggestionId = null;
            }
            else
            {
                Draft = null;
            }
        }

        var source = new CancellationTokenSource();
        _ = RunAsync(request, kind, draftId, requestId, focusQuestion, triggerSegmentId, automatic, replacing, source.Token);
        return source;
    }

    private async Task RunAsync(
        AssistantRequest request,
        SuggestionKind kind,
        Guid draftId,
        Guid requestId,
        string? fallbackQuestion,
        Guid? triggerSegmentId,
        bool automatic,
        Suggestion? replacing,
        CancellationToken token)
    {
        var usedScreen = request.ScreenContext is not null;
        try
        {
            var output = await _assistant.GenerateAsync(
                request,
                partial => UpdateDraft(partial, kind, draftId, requestId, automatic, fallbackQuestion, triggerSegmentId),
                token);
            token.ThrowIfCancellationRequested();
            if (usedScreen) PendingScreenContext = null;
            Accept(output, kind, draftId, fallbackQuestion, triggerSegmentId, automatic, replacing, usedScreen);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            if (token.IsCancellationRequested) return;
            Log.Error("assistant", "Suggestion failed: " + error.Message);
            LastError = error.Message;
        }
        finally
        {
            Finish(kind, requestId);
        }
    }

    private void Finish(SuggestionKind kind, Guid requestId)
    {
        _activeKinds.Remove(kind);
        NotifyGenerating();
        if (_draftRequestId == requestId)
        {
            Draft = null;
            _draftRequestId = null;
        }
    }

    private void UpdateDraft(AssistantPartial partial, SuggestionKind kind, Guid id, Guid requestId, bool automatic, string? fallbackQuestion, Guid? triggerSegmentId)
    {
        if (_draftRequestId != requestId) return;
        // For automatic triggers, only show something once the model confirms there is a question.
        if (automatic && partial.QuestionDetected != true) return;
        var question = string.IsNullOrEmpty(partial.Question) ? fallbackQuestion : partial.Question;
        if (Draft is null) SelectedSuggestionId = null;
        Draft = new Suggestion
        {
            Id = id,
            Kind = kind,
            Question = question,
            Text = partial.Answer,
            Explanation = string.IsNullOrEmpty(partial.Explanation) ? null : partial.Explanation,
            TriggerSegmentId = triggerSegmentId,
            IsAutomatic = automatic,
        };
    }

    private void Accept(
        AssistantOutput output,
        SuggestionKind kind,
        Guid id,
        string? fallbackQuestion,
        Guid? triggerSegmentId,
        bool automatic,
        Suggestion? replacing,
        bool usedScreen)
    {
        if (!string.IsNullOrEmpty(output.MeetingTopic)) MeetingTopic = output.MeetingTopic;
        LastError = null;

        // The model decided there was nothing to answer.
        if (automatic && (!output.QuestionDetected || output.Text.Length == 0)) return;

        var question = output.Question ?? (kind.IsSpoken() ? fallbackQuestion : null);
        var suggestion = new Suggestion
        {
            Id = id,
            Kind = kind,
            Question = question,
            Text = output.Text,
            Explanation = output.Explanation,
            LanguageName = output.Language,
            Confidence = output.Confidence,
            NeedsMoreInfo = output.NeedsMoreInfo,
            DirectedAtUser = output.DirectedAtUser,
            Items = output.Items,
            ActionItems = output.ActionItems,
            OpenQuestions = output.OpenQuestions,
            UsedScreenContext = usedScreen,
            TriggerSegmentId = triggerSegmentId,
            IsAutomatic = automatic,
        };

        var replaceIndex = replacing is null ? -1 : IndexOf(replacing.Id);
        if (replacing is not null && replaceIndex >= 0)
        {
            Suggestions[replaceIndex] = suggestion with { Id = replacing.Id };
            SelectedSuggestionId = replaceIndex == 0 ? null : replacing.Id;
            NotifyDisplay();
            return;
        }

        // An automatic answer to a question we already answered replaces the old card.
        if (automatic && question is not null)
        {
            for (var index = 0; index < Suggestions.Count; index++)
            {
                var existing = Suggestions[index];
                if (existing.Kind == SuggestionKind.Answer && existing.Question is { } existingQuestion
                    && TextSimilarity.Jaccard(existingQuestion, question) >= 0.8)
                {
                    Suggestions.RemoveAt(index);
                    break;
                }
            }
        }

        Suggestions.Insert(0, suggestion);
        while (Suggestions.Count > 60) Suggestions.RemoveAt(Suggestions.Count - 1);
        SelectedSuggestionId = null;
        NotifyDisplay();
    }

    private List<string> PreviousAnswers(Suggestion? replacing)
    {
        var answers = Suggestions
            .Where(s => s.Kind.IsSpoken())
            .Take(4)
            .Select(s => s.Text)
            .ToList();
        if (replacing is not null && replacing.Text.Length > 0)
        {
            answers.RemoveAll(a => a == replacing.Text);
            answers.Insert(0, replacing.Text);
        }
        return answers;
    }

    private int IndexOf(Guid id)
    {
        for (var index = 0; index < Suggestions.Count; index++)
        {
            if (Suggestions[index].Id == id) return index;
        }
        return -1;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private void NotifyDisplay()
    {
        OnPropertyChanged(nameof(CurrentSuggestion));
        OnPropertyChanged(nameof(DisplayedSuggestion));
        OnPropertyChanged(nameof(IsStreamingCurrent));
        OnPropertyChanged(nameof(HistoryPosition));
        OnPropertyChanged(nameof(HistoryLabel));
        OnPropertyChanged(nameof(CanShowOlder));
        OnPropertyChanged(nameof(CanShowNewer));
    }

    private void NotifyGenerating()
    {
        OnPropertyChanged(nameof(IsGenerating));
        OnPropertyChanged(nameof(IsGeneratingRecap));
        OnPropertyChanged(nameof(IsGeneratingWhatToSay));
        OnPropertyChanged(nameof(IsGeneratingQuestions));
    }
}
