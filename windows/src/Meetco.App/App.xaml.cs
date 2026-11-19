using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Meetco.Core.Anthropic;
using Meetco.Core.Assistant;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;
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
    private ApiKeyRegistry? _keys;
    private IReadOnlyDictionary<AssistantProvider, IModelLister>? _listers;
    private WhisperModelStore? _whisperModels;
    private WhisperTranscriptionService? _whisper;
    private readonly List<IDisposable> _disposables = [];
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
    private bool _started;

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

        try
        {
            BuildServices(appData);
            BuildWindows();

            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
                (_, _) => Dispatcher.BeginInvoke(new Action(() => _shell?.ShowEverything())),
                null, Timeout.Infinite, false);
        }
        catch (Exception error)
        {
            // Without this, a failed start would leave an invisible process holding the
            // single-instance lock, and every later launch would silently exit.
            Log.Error("app", "Startup failed: " + error);
            MessageBox.Show("Meetco could not start:\n\n" + error.Message + "\n\nDetails are in " +
                Path.Combine(localData, "logs", "meetco.log"), "Meetco", MessageBoxButton.OK, MessageBoxImage.Error);
            Quit();
            return;
        }

        _started = true;
        Log.Info("app", "Meetco launched");
    }

    private void BuildServices(string appData)
    {
        var localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meetco");
        var settings = AppSettings.Load(Path.Combine(appData, "settings.json"));
        var keyProvider = new ApiKeyProvider(new CredentialManagerKeyStore());
        var keys = new ApiKeyRegistry(keyProvider, provider => new CredentialManagerKeyStore(provider));
        var client = new OpenAIClient(keyProvider, () => settings.NetworkConfiguration);

        // One client per assistant provider; everything but Anthropic speaks the OpenAI protocol.
        var chatClients = new Dictionary<AssistantProvider, IOpenAIClient> { [AssistantProvider.OpenAI] = client };
        var listers = new Dictionary<AssistantProvider, IModelLister> { [AssistantProvider.OpenAI] = client };
        foreach (var provider in Enum.GetValues<AssistantProvider>().Where(p => p != AssistantProvider.OpenAI && p.Wire() == ChatWire.OpenAICompatible))
        {
            var providerClient = new OpenAIClient(keys.For(provider), () => settings.ConfigurationFor(provider), requiresApiKey: provider.RequiresApiKey());
            _disposables.Add(providerClient);
            chatClients[provider] = providerClient;
            listers[provider] = providerClient;
        }
        var anthropic = new AnthropicClient(keys.For(AssistantProvider.Anthropic), () => settings.ConfigurationFor(AssistantProvider.Anthropic));
        _disposables.Add(anthropic);
        listers[AssistantProvider.Anthropic] = anthropic;

        var whisperModels = new WhisperModelStore(Path.Combine(localData, "models"));
        var whisper = new WhisperTranscriptionService(whisperModels, () => settings.WhisperModel);
        var transcript = new TranscriptStore();
        var liveTranscription = new LiveTranscriptionHub(keyProvider, () => settings.NetworkConfiguration);
        var transcription = new TranscriptionCoordinator(
            new TranscriptionRouter(
                new LiveFirstTranscriptionService(liveTranscription, new OpenAITranscriptionService(client)),
                whisper),
            transcript,
            settings);
        var suggestions = new SuggestionEngine(
            new ProviderRoutingAssistant(chatClients, anthropic, settings.ConfigurationFor),
            new ScreenshotService(),
            transcript,
            settings,
            keys);
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
            () => WarmUpAsync(client, listers, settings),
            new UiDispatcher(new DispatcherSynchronizationContext(Dispatcher)),
            // Loading the model reads a few hundred MB: keep it off the UI thread.
            token => Task.Run(() => whisper.PrepareAsync(token), token));

        _settings = settings;
        _keyProvider = keyProvider;
        _client = client;
        _keys = keys;
        _listers = listers;
        _whisperModels = whisperModels;
        _whisper = whisper;
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

        var canTranscribe = _settings!.TranscriptionProvider == TranscriptionProvider.OnDevice || _keyProvider!.HasKey;
        if (!canTranscribe || !_settings.HasCompletedOnboarding) ShowWelcome();
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
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meetco", "logs", "meetco.log"),
            _keys!, _listers!, _whisperModels!);

    /// <summary>Opens connections ahead of time so the first transcription and answer skip the TLS handshake.</summary>
    private static async Task WarmUpAsync(OpenAIClient openAI, IReadOnlyDictionary<AssistantProvider, IModelLister> listers, AppSettings settings)
    {
        var transcription = settings.TranscriptionProvider == TranscriptionProvider.OpenAI ? openAI.WarmUpAsync() : Task.CompletedTask;
        var provider = settings.AssistantProvider;
        if (provider != AssistantProvider.OpenAI)
        {
            try
            {
                await listers[provider].ListModelsAsync(null, CancellationToken.None);
            }
            catch
            {
                // Best effort only.
            }
        }
        else if (settings.TranscriptionProvider != TranscriptionProvider.OpenAI)
        {
            await openAI.WarmUpAsync();
        }
        await transcription;
    }

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
        if (_controlBar is not null) _controlBar.AllowClose = true;
        if (_panel is not null) _panel.AllowClose = true;
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows is signing out or shutting down: let every window close.
        if (_controlBar is not null) _controlBar.AllowClose = true;
        if (_panel is not null) _panel.AllowClose = true;
        base.OnSessionEnding(e);
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
            _whisper?.Dispose();
            foreach (var disposable in _disposables) disposable.Dispose();
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
        if (!_started) Quit();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("app", "Unobserved task exception: " + e.Exception.GetBaseException().Message);
        e.SetObserved();
    }
}
