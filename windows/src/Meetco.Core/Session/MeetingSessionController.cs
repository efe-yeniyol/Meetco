using Meetco.Core.Audio;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;
using Meetco.Core.Settings;
using Meetco.Core.Transcription;

namespace Meetco.Core.Session;

public enum SessionState { Idle, Starting, Listening, Stopping }

/// <summary>
/// Owns a listening session: starts/stops audio capture, chunks audio per source, forwards chunks
/// to transcription and keeps UI-facing session state. Create and use on the UI thread; audio
/// callbacks are marshalled back through a <see cref="UiDispatcher"/>.
/// </summary>
public sealed class MeetingSessionController : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly IAudioCaptureFactory _captureFactory;
    private readonly ApiKeyProvider _keyProvider;
    private readonly TranscriptionCoordinator _transcription;
    private readonly TranscriptStore _transcript;
    private readonly SuggestionEngine _suggestions;
    private readonly SessionArchive? _archive;
    private readonly LiveTranscriptionHub? _liveTranscription;
    private readonly Func<Task>? _warmUp;
    private readonly Func<CancellationToken, Task>? _prepareOnDevice;
    private readonly UiDispatcher _dispatcher;

    private readonly Dictionary<AudioSourceKind, IAudioCapture> _captures = [];
    private readonly Dictionary<AudioSourceKind, ChunkingProcessor> _processors = [];
    private readonly Dictionary<AudioSourceKind, long> _lastLevelUpdate = [];
    private CancellationTokenSource? _idleFlush;
    private int _systemRestartAttempts;
    /// <summary>Incremented on every start/stop so callbacks from an old capture are ignored.</summary>
    private int _captureGeneration;

    private SessionState _state = SessionState.Idle;
    private DateTimeOffset? _startedAt;
    private AudioCaptureMode? _activeMode;
    private float _systemLevel;
    private float _microphoneLevel;
    private string? _errorMessage;
    private string? _warningMessage;
    private bool _isMicrophoneMuted;

    public MeetingSessionController(
        AppSettings settings,
        IAudioCaptureFactory captureFactory,
        ApiKeyProvider keyProvider,
        TranscriptionCoordinator transcription,
        TranscriptStore transcript,
        SuggestionEngine suggestions,
        SessionArchive? archive = null,
        LiveTranscriptionHub? liveTranscription = null,
        Func<Task>? warmUp = null,
        UiDispatcher? dispatcher = null,
        Func<CancellationToken, Task>? prepareOnDevice = null)
    {
        _prepareOnDevice = prepareOnDevice;
        _settings = settings;
        _captureFactory = captureFactory;
        _keyProvider = keyProvider;
        _transcription = transcription;
        _transcript = transcript;
        _suggestions = suggestions;
        _archive = archive;
        _liveTranscription = liveTranscription;
        _warmUp = warmUp;
        _dispatcher = dispatcher ?? new UiDispatcher();

        _transcription.SegmentAdded += segment =>
            _suggestions.HandleNewSegment(segment, _activeMode ?? _settings.CaptureMode);
    }

    // MARK: Observable state

    public SessionState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsListening));
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    public bool IsListening => State == SessionState.Listening;
    public bool IsBusy => State is SessionState.Starting or SessionState.Stopping;
    public DateTimeOffset? StartedAt { get => _startedAt; private set => SetProperty(ref _startedAt, value); }
    public AudioCaptureMode? ActiveMode { get => _activeMode; private set => SetProperty(ref _activeMode, value); }
    public IReadOnlyCollection<AudioSourceKind> ActiveSources => _processors.Keys;
    public float SystemLevel { get => _systemLevel; private set => SetProperty(ref _systemLevel, value); }
    public float MicrophoneLevel { get => _microphoneLevel; private set => SetProperty(ref _microphoneLevel, value); }
    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public string? WarningMessage { get => _warningMessage; private set => SetProperty(ref _warningMessage, value); }

    public bool IsMicrophoneMuted
    {
        get => _isMicrophoneMuted;
        set
        {
            if (!SetProperty(ref _isMicrophoneMuted, value)) return;
            if (_processors.TryGetValue(AudioSourceKind.Microphone, out var processor)) processor.SetMuted(value);
            if (value) MicrophoneLevel = 0;
        }
    }

    // MARK: Public controls

    public void ToggleListening()
    {
        switch (State)
        {
            case SessionState.Idle: Start(); break;
            case SessionState.Listening: Stop(); break;
        }
    }

    public void Start()
    {
        if (State != SessionState.Idle) return;
        Run(() => StartSessionAsync(_settings.CaptureMode, isRestart: false));
    }

    public void Stop()
    {
        if (State is not (SessionState.Listening or SessionState.Starting)) return;
        Run(() => StopSessionAsync(archive: true));
    }

    /// <summary>Changes the capture mode; restarts capture if a session is running.</summary>
    public void SetCaptureMode(AudioCaptureMode mode)
    {
        _settings.CaptureMode = mode;
        if (State != SessionState.Listening) return;
        Run(async () =>
        {
            await StopSessionAsync(archive: false);
            await StartSessionAsync(mode, isRestart: true);
        });
    }

    /// <summary>Runs a lifecycle step; an unexpected failure becomes an error message instead of a stuck state.</summary>
    private async void Run(Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception error)
        {
            Log.Error("audio", "Session step failed: " + error);
            StopCaptures();
            _processors.Clear();
            OnPropertyChanged(nameof(ActiveSources));
            ErrorMessage = error.Message;
            State = SessionState.Idle;
        }
    }

    public void DismissMessages()
    {
        ErrorMessage = null;
        WarningMessage = null;
    }

    /// <summary>Synchronous best-effort teardown used at app exit.</summary>
    public void Dispose()
    {
        // Quitting while listening still keeps the transcript when the user asked for history.
        if (State == SessionState.Listening && StartedAt is { } started && _settings.SaveTranscriptsLocally && _archive is not null)
        {
            SaveArchive(started);
        }
        _idleFlush?.Cancel();
        _liveTranscription?.CloseAll();
        StopCaptures();
    }

    // MARK: Session lifecycle

    private async Task StartSessionAsync(AudioCaptureMode mode, bool isRestart)
    {
        ErrorMessage = null;
        WarningMessage = null;
        if (_settings.TranscriptionProvider == TranscriptionProvider.OpenAI && !_keyProvider.HasKey)
        {
            ErrorMessage = "Add your OpenAI API key in Settings, or choose on-device transcription in Settings → Audio, before you start listening.";
            return;
        }
        State = SessionState.Starting;
        if (_settings.TranscriptionProvider == TranscriptionProvider.OnDevice && _prepareOnDevice is { } prepare)
        {
            // Loads the speech model now so the first utterance doesn't wait for it.
            try
            {
                await prepare(CancellationToken.None);
            }
            catch (Exception error)
            {
                ErrorMessage = error.Message;
                State = SessionState.Idle;
                return;
            }
        }
        // Stopped while the model was loading.
        if (State != SessionState.Starting) return;
        // Open the HTTPS connection now so the first transcription/answer doesn't pay for the TLS handshake.
        if (_warmUp is { } warmUp) _ = Task.Run(warmUp);

        if (!isRestart && _settings.ClearTranscriptOnNewSession)
        {
            _transcript.Clear();
            _suggestions.Reset();
        }

        var configuration = _settings.LatencyProfile.ChunkerConfiguration();
        var problems = new List<string>();
        _captureGeneration += 1;

        if (mode.IncludesMicrophone())
        {
            if (_captureFactory.CanUseMicrophone(out var problem))
            {
                TryAttach(() => _captureFactory.CreateMicrophoneCapture(_settings.MicrophoneDeviceId), configuration, problems);
            }
            else
            {
                problems.Add(problem ?? "Meetco can't use the microphone.");
            }
        }

        if (mode.IncludesSystemAudio())
        {
            TryAttach(_captureFactory.CreateSystemCapture, configuration, problems);
        }

        // Starting capture is synchronous on Windows; yield so the UI can repaint "Starting…".
        await Task.Yield();

        if (_processors.Count == 0)
        {
            ErrorMessage = string.Join("\n", problems);
            State = SessionState.Idle;
            return;
        }
        if (problems.Count > 0) WarningMessage = string.Join("\n", problems);

        if (!isRestart || StartedAt is null) StartedAt = DateTimeOffset.Now;
        ActiveMode = mode;
        _systemRestartAttempts = 0;
        State = SessionState.Listening;
        StartIdleFlushLoop();
        OnPropertyChanged(nameof(ActiveSources));
        Log.Info("audio", $"Listening started ({mode})");
    }

    private async Task StopSessionAsync(bool archive)
    {
        State = SessionState.Stopping;
        _idleFlush?.Cancel();
        _idleFlush = null;
        _captureGeneration += 1;
        StopCaptures();

        // Flush the last utterance from each source before tearing down.
        foreach (var processor in _processors.Values)
        {
            if (processor.Finish(DateTimeOffset.Now) is { } chunk) _transcription.Process(chunk);
        }
        _processors.Clear();
        // Utterances still waiting on a live transcript fall back to a file upload.
        _liveTranscription?.CloseAll();
        SystemLevel = 0;
        MicrophoneLevel = 0;
        OnPropertyChanged(nameof(ActiveSources));

        var sessionStart = StartedAt;
        if (archive)
        {
            // Stay in Stopping until the archive is written, so a new session (which may clear the
            // transcript) can't start in between and replace the meeting that just ended.
            if (_settings.SaveTranscriptsLocally && _archive is not null && sessionStart is { } started)
            {
                await _transcription.WaitUntilIdleAsync(TimeSpan.FromSeconds(15));
                SaveArchive(started);
            }
            StartedAt = null;
            ActiveMode = null;
        }
        State = SessionState.Idle;
        Log.Info("audio", "Listening stopped");
    }

    private void SaveArchive(DateTimeOffset startedAt)
    {
        try
        {
            _archive?.Save(new ArchivedSession(startedAt, DateTimeOffset.Now, _suggestions.MeetingTopic, [.. _transcript.Segments], [.. _suggestions.Suggestions]));
        }
        catch (Exception error)
        {
            Log.Error("storage", "Could not save transcript: " + error.Message);
        }
    }

    private void StopCaptures()
    {
        foreach (var capture in _captures.Values)
        {
            try
            {
                capture.Stop();
                capture.Dispose();
            }
            catch (Exception error)
            {
                Log.Error("audio", "Stopping capture failed: " + error.Message);
            }
        }
        _captures.Clear();
    }

    // MARK: Audio plumbing

    private void TryAttach(Func<IAudioCapture> create, AudioChunkerConfiguration configuration, List<string> problems)
    {
        IAudioCapture? capture = null;
        try
        {
            capture = create();
            Attach(capture, configuration);
        }
        catch (Exception error)
        {
            Log.Error("audio", "Capture failed to start: " + error.Message);
            problems.Add(error.Message);
            if (capture is not null)
            {
                _processors.Remove(capture.Kind);
                _captures.Remove(capture.Kind);
                capture.Dispose();
            }
        }
    }

    private void Attach(IAudioCapture capture, AudioChunkerConfiguration configuration)
    {
        var source = capture.Kind;
        if (_captures.Remove(source, out var previous))
        {
            previous.Stop();
            previous.Dispose();
        }

        // With a live model the audio is streamed while it is spoken; chunks only mark utterance ends.
        ILiveAudioSink? liveSink = null;
        if (_settings.TranscriptionProvider == TranscriptionProvider.OpenAI && ModelCatalog.IsLiveTranscriptionModel(_settings.TranscriptionModel))
        {
            liveSink = _liveTranscription?.Open(source, _settings.TranscriptionModel, _settings.TranscriptionLanguage.IsoCode());
        }
        var processor = new ChunkingProcessor(source, configuration, liveSink);
        if (source == AudioSourceKind.Microphone && IsMicrophoneMuted) processor.SetMuted(true);
        _processors[source] = processor;
        _captures[source] = capture;

        var generation = _captureGeneration;
        capture.Start(
            samples =>
            {
                // Audio thread: chunking is cheap and lock-protected; everything else goes to the UI thread.
                var now = DateTimeOffset.Now;
                var output = processor.Ingest(samples, now);
                foreach (var chunk in output.Chunks)
                {
                    // Always transcribed, even if Stop ran meanwhile: it is the end of what was said.
                    _dispatcher.Post(() => _transcription.Process(chunk));
                }
                var ticks = Environment.TickCount64;
                bool shouldUpdate;
                lock (_lastLevelUpdate)
                {
                    shouldUpdate = !_lastLevelUpdate.TryGetValue(source, out var last) || ticks - last >= 80;
                    if (shouldUpdate) _lastLevelUpdate[source] = ticks;
                }
                if (shouldUpdate)
                {
                    var level = output.Level;
                    _dispatcher.Post(() =>
                    {
                        if (generation == _captureGeneration) UpdateLevel(level, source);
                    });
                }
            },
            error => _dispatcher.Post(() =>
            {
                if (generation != _captureGeneration) return;
                UpdateLevel(0, source);
                if (source == AudioSourceKind.System) HandleSystemInterruption(error);
                else if (error is not null) WarningMessage = "Microphone capture stopped: " + error.Message;
            }));
    }

    private void UpdateLevel(float level, AudioSourceKind source)
    {
        if (source == AudioSourceKind.System) SystemLevel = level;
        else MicrophoneLevel = IsMicrophoneMuted ? 0 : level;
    }

    private void StartIdleFlushLoop()
    {
        _idleFlush?.Cancel();
        var source = new CancellationTokenSource();
        _idleFlush = source;
        _ = IdleFlushLoopAsync(source.Token);
    }

    private async Task IdleFlushLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(150, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            foreach (var processor in _processors.Values.ToList())
            {
                if (processor.FlushIfIdle(DateTimeOffset.Now) is { } chunk) _transcription.Process(chunk);
            }
        }
    }

    private async void HandleSystemInterruption(Exception? error)
    {
        if (State != SessionState.Listening) return;
        _processors.Remove(AudioSourceKind.System);
        if (_captures.Remove(AudioSourceKind.System, out var stopped)) stopped.Dispose();
        SystemLevel = 0;
        OnPropertyChanged(nameof(ActiveSources));

        if (_systemRestartAttempts >= 3)
        {
            WarningMessage = "System audio capture was interrupted: " + (error?.Message ?? "the output device changed");
            return;
        }
        _systemRestartAttempts += 1;
        WarningMessage = "System audio was interrupted — reconnecting…";
        var generation = _captureGeneration;
        await Task.Delay(TimeSpan.FromSeconds(1));
        // The session may have been stopped, restarted or switched to mic-only meanwhile.
        if (State != SessionState.Listening || generation != _captureGeneration || ActiveMode?.IncludesSystemAudio() != true) return;
        var problems = new List<string>();
        TryAttach(_captureFactory.CreateSystemCapture, _settings.LatencyProfile.ChunkerConfiguration(), problems);
        // A successful reconnect earns back the retries: switching headphones several times
        // in one meeting must not end system audio for the rest of it.
        if (problems.Count == 0) _systemRestartAttempts = 0;
        WarningMessage = problems.Count == 0 ? null : string.Join("\n", problems);
        OnPropertyChanged(nameof(ActiveSources));
    }
}
