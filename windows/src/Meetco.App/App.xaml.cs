using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Meetco.Core.Assistant;
using Meetco.Core.Infrastructure;
using Meetco.Core.OpenAI;
using Meetco.Core.Session;
using Meetco.Core.Settings;
using Meetco.Core.Transcription;
using MeetcoApp.Platform;
using MeetcoApp.ViewModels;
using MeetcoApp.Views;

namespace MeetcoApp;

/// <summary>Composition root: builds every service once, wires them together and owns the windows.</summary>
public partial class App : Application
{
    private const string InstanceMutexName = @"Local\Meetco.SingleInstance";
    private const string ShowEventName = @"Local\Meetco.Show";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;

    private FileLogger? _logger;
    private AppSettings? _settings;
    private ApiKeyProvider? _keyProvider;
    private OpenAIClient? _client;
    private MeetingSessionController? _session;
    private SessionArchive? _archive;
    private ShellViewModel? _shell;
    private HotKeyManager? _hotKeys;
    private WindowStateStore? _windowStore;
    private ControlBarWindow? _controlBar;
    private MainPanelWindow? _panel;
    private SettingsWindow? _settingsWindow;
    private WelcomeWindow? _welcomeWindow;
    private bool _isQuitting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One Meetco at a time: a second launch just brings the first one back.
        _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch (Exception)
            {
                // The other instance is still starting or shutting down.
            }
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Meetco");
        var localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meetco");
        _logger = new FileLogger(Path.Combine(localData, "logs"));
        Log.Sink = _logger.Write;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("app", "Fatal: " + (args.ExceptionObject as Exception)?.ToString());

        BuildServices(appData);
        BuildWindows();

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(new Action(() => _shell?.ShowEverything())),
            null, Timeout.Infinite, false);

        Log.Info("app", "Meetco launched");
    }

    private void BuildServices(string appData)
    {
        var settings = AppSettings.Load(Path.Combine(appData, "settings.json"));
        var keyProvider = new ApiKeyProvider(new CredentialManagerKeyStore());
        var client = new OpenAIClient(keyProvider, () => settings.NetworkConfiguration);
        var transcript = new TranscriptStore();
        var liveTranscription = new LiveTranscriptionHub(keyProvider, () => settings.NetworkConfiguration);
        var transcription = new TranscriptionCoordinator(
            new LiveFirstTranscriptionService(liveTranscription, new OpenAITranscriptionService(client)),
            transcript,
            settings);
        var suggestions = new SuggestionEngine(
            new OpenAIMeetingAssistant(client),
            new ScreenshotService(),
            transcript,
            settings,
            keyProvider);
        var archive = new SessionArchive(Path.Combine(appData, "Sessions"));
        var session = new MeetingSessionController(
            settings,
            new WasapiAudioCaptureFactory(),
            keyProvider,
            transcription,
            transcript,
            suggestions,
            archive,
            liveTranscription,
            client.WarmUpAsync,
            new UiDispatcher(new DispatcherSynchronizationContext(Dispatcher)));

        _settings = settings;
        _keyProvider = keyProvider;
        _client = client;
        _archive = archive;
        _session = session;
        _windowStore = new WindowStateStore(Path.Combine(appData, "window.json"));
        _shell = new ShellViewModel(settings, session, suggestions, transcription, transcript, ShowSettings, Quit);

        _hotKeys = new HotKeyManager();
        _hotKeys.ActionInvoked += Perform;
        ApplyHotKeys();
        settings.PropertyChanged += OnSettingsChanged;
    }

    private void BuildWindows()
    {
        var shell = _shell!;
        var state = _windowStore!.Load();

        _controlBar = new ControlBarWindow(shell);
        if (state.ControlBarLeft is { } barLeft && state.ControlBarTop is { } barTop && WindowStateStore.IsOnScreen(barLeft, barTop))
        {
            _controlBar.WindowStartupLocation = WindowStartupLocation.Manual;
            _controlBar.Left = barLeft;
            _controlBar.Top = barTop;
            _controlBar.Show();
        }
        else
        {
            // First launch: centered at the top of the main screen once its size is known.
            _controlBar.WindowStartupLocation = WindowStartupLocation.Manual;
            _controlBar.Left = SystemParameters.WorkArea.Left + SystemParameters.WorkArea.Width / 2 - 300;
            _controlBar.Top = SystemParameters.WorkArea.Top + 8;
            _controlBar.Show();
            _controlBar.UpdateLayout();
            _controlBar.Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - _controlBar.ActualWidth) / 2;
        }

        _panel = new MainPanelWindow(shell) { WindowStartupLocation = WindowStartupLocation.Manual };
        var expanded = state.PanelWidth is { } width && state.PanelHeight is { } height && width >= 340 && height >= 170
            ? new Size(width, height)
            : MainPanelWindow.DefaultExpandedSize;
        _panel.ApplyInitialSize(expanded);
        if (state.PanelLeft is { } panelLeft && state.PanelTop is { } panelTop && WindowStateStore.IsOnScreen(panelLeft, panelTop))
        {
            _panel.Left = panelLeft;
            _panel.Top = panelTop;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            _panel.Left = area.Right - _panel.Width - 16;
            _panel.Top = area.Top + 80;
        }
        _panel.Show();

        shell.PropertyChanged += OnShellChanged;

        if (!_keyProvider!.HasKey || !_settings!.HasCompletedOnboarding) ShowWelcome();
    }

    // MARK: Actions

    private void Perform(ShortcutAction action)
    {
        var shell = _shell;
        if (shell is null) return;
        switch (action)
        {
            case ShortcutAction.ToggleListening:
                shell.Session.ToggleListening();
                break;
            case ShortcutAction.WhatShouldISay:
                shell.WhatShouldISayCommand.Execute(null);
                break;
            case ShortcutAction.Recap:
                shell.RecapCommand.Execute(null);
                break;
            case ShortcutAction.TogglePanel:
                shell.TogglePanel();
                break;
            case ShortcutAction.ToggleCompact:
                shell.IsCompact = !shell.IsCompact;
                break;
            case ShortcutAction.ToggleEverything:
                shell.ToggleEverything();
                break;
        }
    }

    private SettingsViewModel CreateSettingsViewModel() =>
        new(_settings!, _keyProvider!, _client!, _archive!, _hotKeys!, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meetco", "logs", "meetco.log"));

    private void ShowSettings()
    {
        if (_settingsWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(CreateSettingsViewModel());
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ShowWelcome()
    {
        if (_welcomeWindow is not null) return;
        _welcomeWindow = new WelcomeWindow(CreateSettingsViewModel());
        _welcomeWindow.Closed += (_, _) => _welcomeWindow = null;
        _welcomeWindow.Show();
        _welcomeWindow.Activate();
    }

    private void Quit()
    {
        if (_isQuitting) return;
        _isQuitting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            SaveWindowState();
            _session?.Dispose();
            _hotKeys?.Dispose();
            _shell?.Stop();
            _showWait?.Unregister(null);
            _showSignal?.Dispose();
            _client?.Dispose();
            Log.Info("app", "Meetco quit");
        }
        catch (Exception error)
        {
            Log.Error("app", "Shutdown error: " + error.Message);
        }
        finally
        {
            if (_instanceMutex is not null)
            {
                try
                {
                    _instanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
                _instanceMutex.Dispose();
            }
        }
        base.OnExit(e);
    }

    // MARK: State sync

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        var shell = _shell!;
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.IsPanelVisible):
                if (shell.IsPanelVisible) _panel?.Show();
                else _panel?.Hide();
                break;
            case nameof(ShellViewModel.IsControlBarVisible):
                if (shell.IsControlBarVisible) _controlBar?.Show();
                else _controlBar?.Hide();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.GlobalShortcutsEnabled):
                ApplyHotKeys();
                break;
            case nameof(AppSettings.HideFromScreenSharing):
                var hidden = _settings!.HideFromScreenSharing;
                if (_controlBar is not null) WindowHelper.SetHiddenFromCapture(_controlBar, hidden);
                if (_panel is not null) WindowHelper.SetHiddenFromCapture(_panel, hidden);
                break;
        }
    }

    private void ApplyHotKeys()
    {
        if (_hotKeys is null || _settings is null) return;
        if (_settings.GlobalShortcutsEnabled) _hotKeys.RegisterAll();
        else _hotKeys.UnregisterAll();
    }

    private void SaveWindowState()
    {
        if (_windowStore is null || _controlBar is null || _panel is null) return;
        var expanded = _panel.ExpandedSize;
        _windowStore.Save(new SavedWindowState
        {
            ControlBarLeft = _controlBar.Left,
            ControlBarTop = _controlBar.Top,
            PanelLeft = _panel.Left,
            PanelTop = _panel.Top,
            PanelWidth = expanded.Width,
            PanelHeight = expanded.Height,
        });
    }

    // MARK: Error handling

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("app", "Unhandled UI exception: " + e.Exception);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("app", "Unobserved task exception: " + e.Exception.GetBaseException().Message);
        e.SetObserved();
    }
}
