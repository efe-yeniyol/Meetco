using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;
using Meetco.Core.Session;
using Meetco.Core.Settings;
using Meetco.Core.Transcription;

namespace MeetcoApp.ViewModels;

/// <summary>
/// UI state shared by the floating windows (like AppState on the Mac) plus the commands they use.
/// Core controllers are exposed directly so XAML binds to their live properties.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly DispatcherTimer _clock;
    private bool _isPanelVisible = true;
    private bool _isControlBarVisible = true;
    private bool _isCompact;
    private bool _isTranscriptTab;
    private bool _isPeopleTab;
    private string _elapsedText = "";

    public ShellViewModel(
        AppSettings settings,
        MeetingSessionController session,
        SuggestionEngine suggestions,
        TranscriptionCoordinator transcription,
        TranscriptStore transcript,
        Action openSettings,
        Action quit)
    {
        Settings = settings;
        Session = session;
        Suggestions = suggestions;
        Transcription = transcription;
        Transcript = transcript;
        _isCompact = settings.DefaultPanelSize == PanelSize.Compact;

        ToggleListeningCommand = new RelayCommand(session.ToggleListening);
        WhatShouldISayCommand = new RelayCommand(() =>
        {
            ShowAnswers();
            suggestions.WhatShouldISay();
        });
        RecapCommand = new RelayCommand(() =>
        {
            ShowAnswers();
            suggestions.Recap();
        });
        QuestionsToAskCommand = new RelayCommand(() =>
        {
            ShowAnswers();
            suggestions.QuestionsToAsk();
        });
        CaptureScreenCommand = new RelayCommand(() =>
        {
            if (suggestions.HasPendingScreenContext) suggestions.PendingScreenContext = null;
            else suggestions.CaptureScreenContext();
        });
        TogglePanelCommand = new RelayCommand(TogglePanel);
        ToggleCompactCommand = new RelayCommand(() => IsCompact = !IsCompact);
        ShowAnswersCommand = new RelayCommand(() => { IsTranscriptTab = false; IsPeopleTab = false; });
        ShowTranscriptCommand = new RelayCommand(() => IsTranscriptTab = true);
        ShowPeopleCommand = new RelayCommand(() => IsPeopleTab = true);
        HidePanelCommand = new RelayCommand(() => IsPanelVisible = false);
        CopyCommand = new RelayCommand(parameter =>
        {
            if (parameter is Suggestion suggestion) CopyText(suggestion.ClipboardText);
        });
        RegenerateCommand = new RelayCommand(parameter =>
        {
            if (parameter is Suggestion suggestion && !suggestions.IsStreamingCurrent) suggestions.Regenerate(suggestion);
        });
        DismissCommand = new RelayCommand(parameter =>
        {
            if (parameter is Suggestion suggestion) suggestions.Dismiss(suggestion);
        });
        ShowOlderCommand = new RelayCommand(suggestions.ShowOlder);
        ShowNewerCommand = new RelayCommand(suggestions.ShowNewer);
        AnswerSegmentCommand = new RelayCommand(parameter =>
        {
            if (parameter is not TranscriptSegment segment) return;
            ShowAnswers();
            suggestions.Answer(segment);
        });
        CopyTranscriptCommand = new RelayCommand(() => CopyText(transcript.PlainText));
        SetCaptureModeCommand = new RelayCommand(parameter =>
        {
            if (parameter is AudioCaptureMode mode) session.SetCaptureMode(mode);
        });
        OpenSettingsCommand = new RelayCommand(openSettings);
        QuitCommand = new RelayCommand(quit);
        DismissSessionMessagesCommand = new RelayCommand(session.DismissMessages);
        DismissSuggestionErrorCommand = new RelayCommand(() => suggestions.LastError = null);
        DismissTranscriptionErrorCommand = new RelayCommand(transcription.ClearError);

        session.PropertyChanged += OnSessionChanged;
        settings.PropertyChanged += OnSettingsChanged;

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateElapsed();
        _clock.Start();
    }

    public AppSettings Settings { get; }
    public MeetingSessionController Session { get; }
    public SuggestionEngine Suggestions { get; }
    public TranscriptionCoordinator Transcription { get; }
    public TranscriptStore Transcript { get; }

    public IReadOnlyList<Option> CaptureModes { get; } =
        Enum.GetValues<AudioCaptureMode>().Select(m => new Option(m, m.ShortTitle())).ToList();

    // MARK: UI state

    public bool IsPanelVisible { get => _isPanelVisible; set => SetProperty(ref _isPanelVisible, value); }
    public bool IsControlBarVisible { get => _isControlBarVisible; set => SetProperty(ref _isControlBarVisible, value); }
    public bool IsCompact { get => _isCompact; set => SetProperty(ref _isCompact, value); }

    public bool IsTranscriptTab
    {
        get => _isTranscriptTab;
        set
        {
            if (!SetProperty(ref _isTranscriptTab, value)) return;
            if (value) IsPeopleTab = false;
            OnPropertyChanged(nameof(IsAnswersTab));
        }
    }

    /// <summary>People and key points Meetco gathered from the conversation.</summary>
    public bool IsPeopleTab
    {
        get => _isPeopleTab;
        set
        {
            if (!SetProperty(ref _isPeopleTab, value)) return;
            if (value) IsTranscriptTab = false;
            OnPropertyChanged(nameof(IsAnswersTab));
        }
    }

    public bool IsAnswersTab => !IsTranscriptTab && !IsPeopleTab;

    /// <summary>Meeting memory (people, key points); null when not configured.</summary>
    public MeetingMemoryEngine? Memory => Suggestions.Memory;

    public string ElapsedText { get => _elapsedText; private set => SetProperty(ref _elapsedText, value); }

    public string ListenButtonText => Session.State switch
    {
        SessionState.Starting => "Starting…",
        SessionState.Listening => "Stop",
        SessionState.Stopping => "Stopping…",
        _ => "Listen",
    };

    public string CaptureModeText => (Session.ActiveMode ?? Settings.CaptureMode).ShortTitle();

    public string ExplanationBadge => Settings.ExplanationLanguage.Badge();

    // MARK: Commands

    public ICommand ToggleListeningCommand { get; }
    public ICommand WhatShouldISayCommand { get; }
    public ICommand RecapCommand { get; }
    public ICommand QuestionsToAskCommand { get; }
    public ICommand CaptureScreenCommand { get; }
    public ICommand TogglePanelCommand { get; }
    public ICommand ToggleCompactCommand { get; }
    public ICommand ShowAnswersCommand { get; }
    public ICommand ShowTranscriptCommand { get; }
    public ICommand ShowPeopleCommand { get; }
    public ICommand HidePanelCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand RegenerateCommand { get; }
    public ICommand DismissCommand { get; }
    public ICommand ShowOlderCommand { get; }
    public ICommand ShowNewerCommand { get; }
    public ICommand AnswerSegmentCommand { get; }
    public ICommand CopyTranscriptCommand { get; }
    public ICommand SetCaptureModeCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand QuitCommand { get; }
    public ICommand DismissSessionMessagesCommand { get; }
    public ICommand DismissSuggestionErrorCommand { get; }
    public ICommand DismissTranscriptionErrorCommand { get; }

    // MARK: Actions (also used by hotkeys)

    public void ShowAnswers()
    {
        IsTranscriptTab = false;
        IsPeopleTab = false;
        IsControlBarVisible = true;
        IsPanelVisible = true;
    }

    public void TogglePanel()
    {
        IsPanelVisible = !IsPanelVisible;
        if (IsPanelVisible) IsControlBarVisible = true;
    }

    public void ToggleEverything()
    {
        var visible = IsControlBarVisible || IsPanelVisible;
        IsControlBarVisible = !visible;
        IsPanelVisible = !visible;
    }

    public void ShowEverything()
    {
        IsControlBarVisible = true;
        IsPanelVisible = true;
    }

    public void Stop() => _clock.Stop();

    private static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception error)
        {
            // The clipboard can be briefly locked by another app.
            Log.Notice("app", "Copy failed: " + error.Message);
        }
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MeetingSessionController.State):
                OnPropertyChanged(nameof(ListenButtonText));
                UpdateElapsed();
                break;
            case nameof(MeetingSessionController.ActiveMode):
                OnPropertyChanged(nameof(CaptureModeText));
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.CaptureMode):
                OnPropertyChanged(nameof(CaptureModeText));
                break;
            case nameof(AppSettings.ExplanationLanguage):
                OnPropertyChanged(nameof(ExplanationBadge));
                break;
        }
    }

    private void UpdateElapsed()
    {
        if (Session.State != SessionState.Listening || Session.StartedAt is not { } started)
        {
            ElapsedText = "";
            return;
        }
        var elapsed = DateTimeOffset.Now - started;
        ElapsedText = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
    }
}
