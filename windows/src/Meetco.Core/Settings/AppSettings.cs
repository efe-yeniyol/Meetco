using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meetco.Core.Assistant;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;

namespace Meetco.Core.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<PanelSize>))]
public enum PanelSize { Compact, Expanded }

/// <summary>
/// User preferences, persisted as JSON (in %APPDATA%\Meetco\settings.json in the app).
/// The API key is NOT stored here (see <see cref="IApiKeyStore"/>).
/// Every change is saved immediately. Defaults match the macOS app.
/// </summary>
public sealed class AppSettings : ObservableObject
{
    private string? _filePath;
    private bool _isLoading;

    // OpenAI
    private string _chatModel = ModelCatalog.DefaultAssistantModel;
    private string _transcriptionModel = ModelCatalog.DefaultTranscriptionModel;
    private string _apiBaseUrl = OpenAIConfiguration.DefaultBaseUrl.AbsoluteUri;
    private ResponseLanguage _responseLanguage = ResponseLanguage.Auto;
    private ExplanationLanguage _explanationLanguage = ExplanationLanguage.Turkish;
    private ResponseStyle _responseStyle = ResponseStyle.Concise;
    private string _userContext = "";
    private bool _autoSuggest = true;

    // Audio & transcription
    private TranscriptionLanguage _transcriptionLanguage = TranscriptionLanguage.Auto;
    private LatencyProfile _latencyProfile = LatencyProfile.Fast;
    private AudioCaptureMode _captureMode = AudioCaptureMode.SystemAndMicrophone;
    private string? _microphoneDeviceId;

    // General
    private PanelSize _defaultPanelSize = PanelSize.Expanded;
    private bool _alwaysOnTop = true;
    private bool _hideFromScreenSharing;
    private bool _globalShortcutsEnabled = true;
    private bool _autoScrollTranscript = true;
    private bool _clearTranscriptOnNewSession = true;

    // Privacy
    private bool _saveTranscriptsLocally;
    private bool _hasCompletedOnboarding;

    public string ChatModel { get => _chatModel; set => Set(ref _chatModel, value); }
    public string TranscriptionModel { get => _transcriptionModel; set => Set(ref _transcriptionModel, value); }
    public string ApiBaseUrl { get => _apiBaseUrl; set => Set(ref _apiBaseUrl, value); }
    public ResponseLanguage ResponseLanguage { get => _responseLanguage; set => Set(ref _responseLanguage, value); }
    public ExplanationLanguage ExplanationLanguage { get => _explanationLanguage; set => Set(ref _explanationLanguage, value); }
    public ResponseStyle ResponseStyle { get => _responseStyle; set => Set(ref _responseStyle, value); }
    public string UserContext { get => _userContext; set => Set(ref _userContext, value); }
    public bool AutoSuggest { get => _autoSuggest; set => Set(ref _autoSuggest, value); }

    public TranscriptionLanguage TranscriptionLanguage { get => _transcriptionLanguage; set => Set(ref _transcriptionLanguage, value); }
    public LatencyProfile LatencyProfile { get => _latencyProfile; set => Set(ref _latencyProfile, value); }
    public AudioCaptureMode CaptureMode { get => _captureMode; set => Set(ref _captureMode, value); }
    public string? MicrophoneDeviceId { get => _microphoneDeviceId; set => Set(ref _microphoneDeviceId, value); }

    public PanelSize DefaultPanelSize { get => _defaultPanelSize; set => Set(ref _defaultPanelSize, value); }
    public bool AlwaysOnTop { get => _alwaysOnTop; set => Set(ref _alwaysOnTop, value); }
    public bool HideFromScreenSharing { get => _hideFromScreenSharing; set => Set(ref _hideFromScreenSharing, value); }
    public bool GlobalShortcutsEnabled { get => _globalShortcutsEnabled; set => Set(ref _globalShortcutsEnabled, value); }
    public bool AutoScrollTranscript { get => _autoScrollTranscript; set => Set(ref _autoScrollTranscript, value); }
    public bool ClearTranscriptOnNewSession { get => _clearTranscriptOnNewSession; set => Set(ref _clearTranscriptOnNewSession, value); }

    public bool SaveTranscriptsLocally { get => _saveTranscriptsLocally; set => Set(ref _saveTranscriptsLocally, value); }
    public bool HasCompletedOnboarding { get => _hasCompletedOnboarding; set => Set(ref _hasCompletedOnboarding, value); }

    [JsonIgnore]
    public AssistantPreferences AssistantPreferences => new(ResponseLanguage, ExplanationLanguage, ResponseStyle, UserContext);

    /// <summary>Thread-safe snapshot of the network configuration.</summary>
    [JsonIgnore]
    public OpenAIConfiguration NetworkConfiguration => OpenAIConfiguration.From(ApiBaseUrl);

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName) && !_isLoading) Save();
    }

    // MARK: Persistence

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Loads settings from a JSON file (missing or invalid files give defaults) and saves future changes there.</summary>
    public static AppSettings Load(string filePath)
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(filePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath), FileOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Error("storage", "Settings could not be read, using defaults: " + error.Message);
            settings = new AppSettings();
        }
        settings._filePath = filePath;
        return settings;
    }

    public void Save()
    {
        if (_filePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var temporary = _filePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, FileOptions));
            File.Move(temporary, _filePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Error("storage", "Settings could not be saved: " + error.Message);
        }
    }

    /// <summary>Applies several changes and saves once.</summary>
    public void Batch(Action<AppSettings> change)
    {
        _isLoading = true;
        try
        {
            change(this);
        }
        finally
        {
            _isLoading = false;
        }
        Save();
    }
}
