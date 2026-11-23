using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;
using Meetco.Core.Session;
using Meetco.Core.Settings;
using Meetco.Core.Transcription;
using MeetcoApp.Platform;

namespace MeetcoApp.ViewModels;

/// <summary>State and actions of the Settings and Welcome windows.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ApiKeyProvider _keyProvider;
    private readonly IOpenAIClient _client;
    private readonly ApiKeyRegistry _keys;
    private readonly IReadOnlyDictionary<AssistantProvider, IModelLister> _listers;
    private readonly WhisperModelStore _whisperModels;
    private string _providerKeyStatus = "";
    private string _providerTestResult = "";
    private bool _isTestingProvider;
    private IReadOnlyList<string> _serverModels = [];
    private string _whisperStatus = "";
    private bool _isDownloading;
    private double _downloadProgress;
    private CancellationTokenSource? _download;
    private readonly SessionArchive _archive;
    private readonly HotKeyManager _hotKeys;
    private string _keyStatus = "";
    private string _testResult = "";
    private bool _isTesting;
    private int _sessionCount;

    public SettingsViewModel(AppSettings settings, ApiKeyProvider keyProvider, IOpenAIClient client, SessionArchive archive, HotKeyManager hotKeys, string logFile,
        ApiKeyRegistry keys, IReadOnlyDictionary<AssistantProvider, IModelLister> listers, WhisperModelStore whisperModels)
    {
        Settings = settings;
        _keyProvider = keyProvider;
        _client = client;
        _keys = keys;
        _listers = listers;
        _whisperModels = whisperModels;
        settings.PropertyChanged += OnSettingsChanged;
        RefreshProviderKeyStatus();
        RefreshWhisperStatus();
        _archive = archive;
        _hotKeys = hotKeys;
        LogFile = logFile;
        Microphones = BuildMicrophoneOptions();
        RefreshKeyStatus();
        RefreshSessionCount();
    }

    public AppSettings Settings { get; }
    public string LogFile { get; }

    // MARK: Options

    public IReadOnlyList<string> AssistantModels { get; } = ModelCatalog.AssistantModels.Select(m => m.Id).ToList();
    public IReadOnlyList<string> TranscriptionModels { get; } = ModelCatalog.TranscriptionModels.Select(m => m.Id).ToList();

    public string AssistantModelNotes { get; } =
        string.Join("\n", ModelCatalog.AssistantModels.Select(m => $"{m.Id}: {m.Note}"));

    public string TranscriptionModelNotes { get; } =
        string.Join("\n", ModelCatalog.TranscriptionModels.Select(m => $"{m.Id}: {m.Note}"));

    public IReadOnlyList<Option> ResponseLanguages { get; } =
        LanguageChoice.AllChoices<ResponseLanguage>().Select(v => new Option(v, v.Title())).ToList();

    public IReadOnlyList<Option> ExplanationLanguages { get; } =
        LanguageChoice.AllChoices<ExplanationLanguage>().Select(v => new Option(v, v.Title())).ToList();

    public string LanguageHint { get; } =
        $"Meetco works in {SpokenLanguage.All.Count} languages and answers in the speaker's language.";

    public IReadOnlyList<Option> ResponseStyles { get; } =
        Enum.GetValues<ResponseStyle>().Select(v => new Option(v, v.ToString())).ToList();

    public IReadOnlyList<Option> TranscriptionLanguages { get; } =
        LanguageChoice.AllChoices<TranscriptionLanguage>().Select(v => new Option(v, v.Title())).ToList();

    public IReadOnlyList<Option> LatencyProfiles { get; } =
        Enum.GetValues<LatencyProfile>().Select(v => new Option(v, v.ToString() + " — " + v.Detail())).ToList();

    public IReadOnlyList<Option> CaptureModes { get; } =
        Enum.GetValues<AudioCaptureMode>().Select(v => new Option(v, v.Title())).ToList();

    public IReadOnlyList<Option> GlassTints { get; } =
        Enum.GetValues<GlassTint>().Select(v => new Option(v, v.Title())).ToList();

    public IReadOnlyList<Option> PanelSizes { get; } =
        Enum.GetValues<PanelSize>().Select(v => new Option(v, v.ToString())).ToList();

    /// <summary>Microphones; the empty id stands for the system default.</summary>
    public IReadOnlyList<Option> Microphones { get; private set; }

    public string MicrophoneChoice
    {
        get => Settings.MicrophoneDeviceId ?? "";
        set
        {
            Settings.MicrophoneDeviceId = string.IsNullOrEmpty(value) ? null : value;
            OnPropertyChanged();
        }
    }

    public string ShortcutList => string.Join("\n", ShortcutCatalog.All.Select(a => $"{a.DisplayString(),-14}  {a.Title()}"))
        + (_hotKeys.Conflicts.Count > 0
            ? "\n\nAlready used by another app: " + string.Join(", ", _hotKeys.Conflicts.Select(c => c.DisplayString()))
            : "");

    // MARK: API key

    public string KeyStatus { get => _keyStatus; private set => SetProperty(ref _keyStatus, value); }
    public string TestResult { get => _testResult; private set => SetProperty(ref _testResult, value); }
    public bool IsTesting { get => _isTesting; private set => SetProperty(ref _isTesting, value); }
    public bool HasKey => _keyProvider.HasKey;

    public bool SaveKey(string key)
    {
        key = key.Trim();
        if (key.Length == 0)
        {
            TestResult = "Paste your OpenAI API key first.";
            return false;
        }
        try
        {
            _keyProvider.Save(key);
            TestResult = "Saved to Windows Credential Manager.";
            RefreshKeyStatus();
            return true;
        }
        catch (Exception error)
        {
            TestResult = error.Message;
            return false;
        }
    }

    public void DeleteKey()
    {
        try
        {
            _keyProvider.Delete();
            TestResult = "The API key was deleted.";
        }
        catch (Exception error)
        {
            TestResult = error.Message;
        }
        RefreshKeyStatus();
    }

    /// <summary>Tests the typed key, or the saved one when nothing is typed.</summary>
    public async Task TestConnectionAsync(string typedKey)
    {
        if (IsTesting) return;
        var key = string.IsNullOrWhiteSpace(typedKey) ? null : typedKey.Trim();
        if (key is null && !_keyProvider.HasKey)
        {
            TestResult = "Paste your OpenAI API key first.";
            return;
        }
        IsTesting = true;
        TestResult = "Testing…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var models = await _client.ListModelsAsync(key, timeout.Token);
            var hasChat = models.Contains(Settings.ChatModel);
            TestResult = $"Connected. {models.Count} models available."
                + (hasChat ? "" : $" Note: \"{Settings.ChatModel}\" is not in your model list.");
        }
        catch (OperationCanceledException)
        {
            TestResult = "The connection timed out.";
        }
        catch (Exception error)
        {
            TestResult = error.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void RefreshKeyStatus()
    {
        var key = _keyProvider.CurrentKey();
        KeyStatus = string.IsNullOrEmpty(key) ? "No API key saved." : "Saved key: " + ApiKeyProvider.Mask(key);
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(CanGetStarted));
    }

    // MARK: Assistant provider

    public IReadOnlyList<Option> AssistantProviders { get; } =
        Enum.GetValues<AssistantProvider>().Select(p => new Option(p, p.Title())).ToList();

    private AssistantProvider Provider => Settings.AssistantProvider;

    public bool ProviderIsOpenAI => Provider == AssistantProvider.OpenAI;
    public bool ProviderUsesOwnKey => !ProviderIsOpenAI;

    public string ProviderKeyLabel => Provider.RequiresApiKey() ? $"{Provider.ShortName()} API key" : $"{Provider.ShortName()} API key (optional)";

    public string ProviderHint => Provider switch
    {
        AssistantProvider.OpenAI => "Uses the same OpenAI key as transcription (OpenAI tab).",
        AssistantProvider.Anthropic => "Claude via Anthropic's API, billed to your Anthropic account. Create a key at console.anthropic.com/settings/keys.",
        AssistantProvider.DeepSeek => "Billed to your DeepSeek account. DeepSeek does not read screenshots, so screen context is skipped. Create a key at platform.deepseek.com/api_keys.",
        AssistantProvider.Ollama => "Runs on this PC: answers never leave your computer. Install Ollama (ollama.com), run \"ollama pull qwen3\" and keep it running. Pair it with on-device transcription (Audio tab) to keep everything local.",
        AssistantProvider.LMStudio => "Runs on this PC: answers never leave your computer. Start LM Studio's local server with a model loaded. Pair it with on-device transcription (Audio tab) to keep everything local.",
        _ => "Any server that speaks the OpenAI Chat Completions API: OpenRouter, Groq, Mistral, vLLM, llama.cpp and others.",
    };

    public string BaseUrlHint => Provider.RequiresApiKey()
        ? $"Default: {Provider.DefaultBaseUrl()}. Your API key is sent to this address."
        : $"Default: {Provider.DefaultBaseUrl()}. Your meeting transcript is sent to this address.";

    /// <summary>Presets plus the models the server reported.</summary>
    public IReadOnlyList<string> ProviderModelOptions =>
        Provider.ModelPresets().Select(m => m.Id).Concat(_serverModels).Distinct().ToList();

    public string ProviderModelNotes => Provider.ModelPresets().Count > 0
        ? string.Join("\n", Provider.ModelPresets().Select(m => $"{m.Id}: {m.Note}"))
        : _serverModels.Count > 0 ? "Models available on the server are listed above." : "Press Test Connection to load the models available on the server.";

    public string ProviderKeyStatus { get => _providerKeyStatus; private set => SetProperty(ref _providerKeyStatus, value); }
    public string ProviderTestResult { get => _providerTestResult; private set => SetProperty(ref _providerTestResult, value); }
    public bool IsTestingProvider { get => _isTestingProvider; private set => SetProperty(ref _isTestingProvider, value); }

    public bool SaveProviderKey(string key)
    {
        key = key.Trim();
        if (key.Length == 0)
        {
            ProviderTestResult = $"Paste your {Provider.ShortName()} API key first.";
            return false;
        }
        try
        {
            _keys.For(Provider).Save(key);
            ProviderTestResult = "Saved to Windows Credential Manager.";
            RefreshProviderKeyStatus();
            return true;
        }
        catch (Exception error)
        {
            ProviderTestResult = error.Message;
            return false;
        }
    }

    public void DeleteProviderKey()
    {
        try
        {
            _keys.For(Provider).Delete();
            ProviderTestResult = "The API key was deleted.";
        }
        catch (Exception error)
        {
            ProviderTestResult = error.Message;
        }
        RefreshProviderKeyStatus();
    }

    /// <summary>Lists the provider's models with the typed key, or the saved one, and offers them in the model box.</summary>
    public async Task TestProviderAsync(string typedKey)
    {
        if (IsTestingProvider) return;
        var provider = Provider;
        var key = string.IsNullOrWhiteSpace(typedKey) ? null : typedKey.Trim();
        if (key is null && !_keys.IsReady(provider))
        {
            ProviderTestResult = $"Paste your {provider.ShortName()} API key first.";
            return;
        }
        IsTestingProvider = true;
        ProviderTestResult = "Testing…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var models = await _listers[provider].ListModelsAsync(key, timeout.Token);
            if (provider != Provider) return;
            _serverModels = models;
            OnPropertyChanged(nameof(ProviderModelOptions));
            OnPropertyChanged(nameof(ProviderModelNotes));
            if (string.IsNullOrWhiteSpace(Settings.AssistantModel) && models.Count > 0) Settings.AssistantModel = models[0];
            ProviderTestResult = $"Connected to {provider.ShortName()}. {models.Count} models available."
                + (models.Count > 0 && !models.Contains(Settings.AssistantModel) ? $" Note: \"{Settings.AssistantModel}\" is not in the list." : "");
        }
        catch (OperationCanceledException)
        {
            ProviderTestResult = "The connection timed out.";
        }
        catch (OpenAIException error)
        {
            ProviderTestResult = new Meetco.Core.Assistant.ProviderException(provider, error, Settings.ConfigurationFor(provider).BaseUrl).Message;
        }
        catch (Exception error)
        {
            ProviderTestResult = error.Message;
        }
        finally
        {
            IsTestingProvider = false;
        }
    }

    private void RefreshProviderKeyStatus()
    {
        var key = _keys.For(Provider).CurrentKey();
        ProviderKeyStatus = !string.IsNullOrEmpty(key)
            ? "Saved key: " + ApiKeyProvider.Mask(key)
            : Provider.RequiresApiKey() ? "No API key saved." : "No key needed for most local servers.";
    }

    // MARK: Transcription engine

    public IReadOnlyList<Option> TranscriptionProviders { get; } =
    [
        new(TranscriptionProvider.OpenAI, "OpenAI (cloud)"),
        new(TranscriptionProvider.OnDevice, "On this PC (Whisper; private, free)"),
    ];

    public IReadOnlyList<Option> WhisperModelOptions { get; } =
        WhisperModelInfo.All.Select(m => new Option(m.Size, $"{m.Title} ({m.SizeText}) — {m.Note}")).ToList();

    public bool UsesOnDeviceTranscription => Settings.TranscriptionProvider == TranscriptionProvider.OnDevice;
    public bool IsWhisperModelInstalled => _whisperModels.IsInstalled(Settings.WhisperModel);
    public bool CanDownloadWhisper => !IsWhisperModelInstalled && !IsDownloading;

    public string WhisperStatus { get => _whisperStatus; private set => SetProperty(ref _whisperStatus, value); }
    public bool IsDownloading { get => _isDownloading; private set { if (SetProperty(ref _isDownloading, value)) OnPropertyChanged(nameof(CanDownloadWhisper)); } }
    public double DownloadProgress { get => _downloadProgress; private set => SetProperty(ref _downloadProgress, value); }

    public async Task DownloadWhisperModelAsync()
    {
        if (IsDownloading) return;
        var size = Settings.WhisperModel;
        var model = WhisperModelInfo.For(size);
        _download = new CancellationTokenSource();
        IsDownloading = true;
        DownloadProgress = 0;
        WhisperStatus = $"Downloading {model.Title} model ({model.SizeText})…";
        try
        {
            var progress = new Progress<double>(value => DownloadProgress = value * 100);
            await _whisperModels.DownloadAsync(size, progress, _download.Token);
            WhisperStatus = "";
        }
        catch (OperationCanceledException)
        {
            WhisperStatus = "Download canceled.";
        }
        catch (Exception error)
        {
            WhisperStatus = "Download failed: " + error.Message;
        }
        finally
        {
            IsDownloading = false;
            _download.Dispose();
            _download = null;
            RefreshWhisperStatus(keepMessage: true);
        }
    }

    public void CancelDownload() => _download?.Cancel();

    private void RefreshWhisperStatus(bool keepMessage = false)
    {
        OnPropertyChanged(nameof(IsWhisperModelInstalled));
        OnPropertyChanged(nameof(CanDownloadWhisper));
        if (keepMessage && WhisperStatus.Length > 0) return;
        var model = WhisperModelInfo.For(Settings.WhisperModel);
        WhisperStatus = IsWhisperModelInstalled
            ? $"{model.Title} model installed. Speech is transcribed on this PC and never leaves it."
            : $"The {model.Title} model ({model.SizeText}) needs to be downloaded once.";
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.AssistantProvider):
                _serverModels = [];
                ProviderTestResult = "";
                RefreshProviderKeyStatus();
                foreach (var name in new[] { nameof(ProviderIsOpenAI), nameof(ProviderUsesOwnKey), nameof(ProviderKeyLabel), nameof(ProviderHint),
                             nameof(BaseUrlHint), nameof(ProviderModelOptions), nameof(ProviderModelNotes) })
                {
                    OnPropertyChanged(name);
                }
                break;
            case nameof(AppSettings.TranscriptionProvider):
                OnPropertyChanged(nameof(UsesOnDeviceTranscription));
                OnPropertyChanged(nameof(CanGetStarted));
                break;
            case nameof(AppSettings.WhisperModel):
                RefreshWhisperStatus();
                break;
        }
    }

    /// <summary>Onboarding can finish once speech can be transcribed: an OpenAI key, or on-device transcription.</summary>
    public bool CanGetStarted => HasKey || UsesOnDeviceTranscription;

    /// <summary>Stops listening to settings changes when the window closes.</summary>
    public void Detach() => Settings.PropertyChanged -= OnSettingsChanged;

    // MARK: Privacy

    public int SessionCount { get => _sessionCount; private set => SetProperty(ref _sessionCount, value); }

    public void RefreshSessionCount() => SessionCount = _archive.SessionCount();

    public void OpenHistoryFolder()
    {
        try
        {
            Directory.CreateDirectory(_archive.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _archive.Directory + "\"") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Log.Error("app", "Could not open the history folder: " + error.Message);
        }
    }

    public void OpenLogFolder()
    {
        try
        {
            var folder = Path.GetDirectoryName(LogFile)!;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Log.Error("app", "Could not open the log folder: " + error.Message);
        }
    }

    public string? DeleteHistory()
    {
        try
        {
            _archive.ClearAll();
            RefreshSessionCount();
            return null;
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }

    // MARK: Helpers

    public void RefreshMicrophones()
    {
        Microphones = BuildMicrophoneOptions();
        OnPropertyChanged(nameof(Microphones));
        OnPropertyChanged(nameof(MicrophoneChoice));
    }

    private static List<Option> BuildMicrophoneOptions()
    {
        var options = new List<Option> { new("", "System default") };
        options.AddRange(WasapiAudioCaptureFactory.ListMicrophones().Select(d => new Option(d.Id, d.Name)));
        return options;
    }
}
