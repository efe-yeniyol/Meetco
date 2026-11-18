using Meetco.Core.Audio;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;
using Meetco.Core.Settings;
using Meetco.Core.Text;

namespace Meetco.Core.Transcription;

/// <summary>
/// Turns audio chunks into transcript segments: calls the transcription service, filters noise,
/// detects language and questions, de-duplicates, and notifies listeners. UI-thread only.
/// </summary>
public sealed class TranscriptionCoordinator : ObservableObject
{
    private readonly ITranscriptionService _service;
    private readonly TranscriptStore _store;
    private readonly AppSettings _settings;
    private readonly QuestionDetector _questionDetector = new();
    private TaskCompletionSource? _idle;
    private int _inFlightCount;
    private string? _lastError;

    public TranscriptionCoordinator(ITranscriptionService service, TranscriptStore store, AppSettings settings)
    {
        _service = service;
        _store = store;
        _settings = settings;
    }

    /// <summary>Raised on the UI thread for every accepted segment.</summary>
    public event Action<TranscriptSegment>? SegmentAdded;

    public int InFlightCount
    {
        get => _inFlightCount;
        private set
        {
            if (SetProperty(ref _inFlightCount, value)) OnPropertyChanged(nameof(IsTranscribing));
        }
    }

    public bool IsTranscribing => InFlightCount > 0;

    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public void ClearError() => LastError = null;

    public void Process(AudioChunk chunk) => _ = ProcessAsync(chunk);

    private async Task ProcessAsync(AudioChunk chunk)
    {
        var options = new TranscriptionOptions(
            _settings.TranscriptionModel,
            _settings.TranscriptionLanguage.IsoCode(),
            _store.RecentText(chunk.Source, 200));
        InFlightCount += 1;
        Log.Debug("transcription", $"Transcribing {chunk.Source} chunk #{chunk.Sequence} ({chunk.Duration:0.0}s)");
        try
        {
            var result = await _service.TranscribeAsync(chunk, options, CancellationToken.None);
            LastError = null;
            Accept(result, chunk);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Error("transcription", "Transcription failed: " + error.Message);
            LastError = error.Message;
        }
        finally
        {
            InFlightCount -= 1;
            if (InFlightCount == 0)
            {
                _idle?.TrySetResult();
                _idle = null;
            }
        }
    }

    /// <summary>Waits (bounded) for outstanding transcriptions, e.g. before archiving a session.</summary>
    public async Task WaitUntilIdleAsync(TimeSpan timeout)
    {
        if (InFlightCount == 0) return;
        _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = _idle.Task;
        await Task.WhenAny(idle, Task.Delay(timeout));
    }

    private void Accept(TranscriptionResult result, AudioChunk chunk)
    {
        var text = TranscriptTextFilter.Clean(result.Text);
        if (text.Length == 0 || TranscriptTextFilter.IsLikelyHallucination(text)) return;

        // Avoid duplicate segments (e.g. a retried request or the model echoing its prompt).
        if (_store.LastSegment(chunk.Source) is { } last
            && (chunk.StartedAt - last.EndedAt).TotalSeconds < 20
            && TextSimilarity.Normalized(last.Text) == TextSimilarity.Normalized(text))
        {
            return;
        }

        var segment = new TranscriptSegment
        {
            Source = chunk.Source,
            Text = text,
            LanguageCode = result.LanguageCode ?? LanguageDetector.Detect(text),
            StartedAt = chunk.StartedAt,
            EndedAt = chunk.EndedAt,
            IsQuestion = _questionDetector.IsQuestion(text),
        };
        _store.Insert(segment);
        SegmentAdded?.Invoke(segment);
    }
}
