using System.Diagnostics;
using System.IO;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;
using Meetco.Core.Session;
using Meetco.Core.Settings;
using MeetcoApp.Platform;

namespace MeetcoApp.ViewModels;

/// <summary>State and actions of the Settings and Welcome windows.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ApiKeyProvider _keyProvider;
    private readonly IOpenAIClient _client;
    private readonly SessionArchive _archive;
    private readonly HotKeyManager _hotKeys;
    private string _keyStatus = "";
    private string _testResult = "";
    private bool _isTesting;
    private int _sessionCount;

    public SettingsViewModel(AppSettings settings, ApiKeyProvider keyProvider, IOpenAIClient client, SessionArchive archive, HotKeyManager hotKeys, string logFile)
    {
        Settings = settings;
        _keyProvider = keyProvider;
        _client = client;
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
        Enum.GetValues<ResponseLanguage>().Select(v => new Option(v, v.Title())).ToList();

    public IReadOnlyList<Option> ExplanationLanguages { get; } =
        Enum.GetValues<ExplanationLanguage>().Select(v => new Option(v, v.Title())).ToList();

    public IReadOnlyList<Option> ResponseStyles { get; } =
        Enum.GetValues<ResponseStyle>().Select(v => new Option(v, v.ToString())).ToList();

    public IReadOnlyList<Option> TranscriptionLanguages { get; } =
        Enum.GetValues<TranscriptionLanguage>().Select(v => new Option(v, v.Title())).ToList();

    public IReadOnlyList<Option> LatencyProfiles { get; } =
        Enum.GetValues<LatencyProfile>().Select(v => new Option(v, v.ToString() + " — " + v.Detail())).ToList();

    public IReadOnlyList<Option> CaptureModes { get; } =
        Enum.GetValues<AudioCaptureMode>().Select(v => new Option(v, v.Title())).ToList();

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
    }

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
