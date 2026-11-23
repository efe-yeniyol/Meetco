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

/// <summary>Colour of the floating glass windows. <see cref="Neutral"/> is the original dark glass.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GlassTint>))]
public enum GlassTint { Neutral, Teal, Orange, Blue, Purple, Green }

public static class GlassTintExtensions
{
    public static string Title(this GlassTint tint) => tint switch
    {
        GlassTint.Neutral => "Black & White",
        _ => tint.ToString(),
    };

    /// <summary>Fully saturated colour of the tint as (r, g, b). Matches the macOS app.</summary>
    public static (byte R, byte G, byte B) Rgb(this GlassTint tint) => tint switch
    {
        GlassTint.Teal => (0x00, 0x99, 0xA3),
        GlassTint.Orange => (0xFA, 0x85, 0x1F),
        GlassTint.Blue => (0x29, 0x6B, 0xF2),
        GlassTint.Purple => (0x8C, 0x4D, 0xE6),
        GlassTint.Green => (0x33, 0xAD, 0x52),
        _ => (0x0A, 0x84, 0xFF),
    };
}

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
    private bool _detectQuestionsInAnyLanguage = true;
    private bool _meetingMemoryEnabled = true;

    // Assistant provider
    private AssistantProvider _assistantProvider = AssistantProvider.OpenAI;
    private Dictionary<string, string> _providerModels = [];
    private Dictionary<string, string> _providerBaseUrls = [];

    // Audio & transcription
    private TranscriptionLanguage _transcriptionLanguage = TranscriptionLanguage.Auto;
    private LatencyProfile _latencyProfile = LatencyProfile.Fast;
    private AudioCaptureMode _captureMode = AudioCaptureMode.SystemAndMicrophone;
    private string? _microphoneDeviceId;
    private TranscriptionProvider _transcriptionProvider = TranscriptionProvider.OpenAI;
    private WhisperModelSize _whisperModel = WhisperModelSize.Small;

    // General
    private PanelSize _defaultPanelSize = PanelSize.Expanded;
    private GlassTint _glassTint = GlassTint.Neutral;
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

    /// <summary>Lets the assistant model decide whether phrases the local detector missed are questions (any language).</summary>
    public bool DetectQuestionsInAnyLanguage { get => _detectQuestionsInAnyLanguage; set => Set(ref _detectQuestionsInAnyLanguage, value); }

    /// <summary>Tracks people (names, roles) and what was said, and uses it in every answer.</summary>
    public bool MeetingMemoryEnabled { get => _meetingMemoryEnabled; set => Set(ref _meetingMemoryEnabled, value); }

    public AssistantProvider AssistantProvider
    {
        get => _assistantProvider;
        set
        {
            Set(ref _assistantProvider, value);
            OnPropertyChanged(nameof(AssistantModel));
            OnPropertyChanged(nameof(AssistantBaseUrl));
        }
    }

    /// <summary>Model per non-OpenAI provider, keyed by provider name. Replaced (never mutated) so background reads are safe.</summary>
    public Dictionary<string, string> ProviderModels { get => _providerModels; set => Set(ref _providerModels, value ?? []); }

    /// <summary>Base URL per non-OpenAI provider, keyed by provider name.</summary>
    public Dictionary<string, string> ProviderBaseUrls { get => _providerBaseUrls; set => Set(ref _providerBaseUrls, value ?? []); }

    public TranscriptionLanguage TranscriptionLanguage { get => _transcriptionLanguage; set => Set(ref _transcriptionLanguage, value); }
    public LatencyProfile LatencyProfile { get => _latencyProfile; set => Set(ref _latencyProfile, value); }
    public AudioCaptureMode CaptureMode { get => _captureMode; set => Set(ref _captureMode, value); }
    public string? MicrophoneDeviceId { get => _microphoneDeviceId; set => Set(ref _microphoneDeviceId, value); }
    public TranscriptionProvider TranscriptionProvider { get => _transcriptionProvider; set => Set(ref _transcriptionProvider, value); }
    public WhisperModelSize WhisperModel { get => _whisperModel; set => Set(ref _whisperModel, value); }

    public PanelSize DefaultPanelSize { get => _defaultPanelSize; set => Set(ref _defaultPanelSize, value); }
    public GlassTint GlassTint { get => _glassTint; set => Set(ref _glassTint, value); }
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

    /// <summary>Model used by a provider. OpenAI keeps its original setting.</summary>
    public string ModelFor(AssistantProvider provider) =>
        provider == AssistantProvider.OpenAI ? ChatModel : ProviderModels.GetValueOrDefault(provider.ToString()) ?? provider.DefaultModel();

    public void SetModel(AssistantProvider provider, string model)
    {
        if (provider == AssistantProvider.OpenAI) ChatModel = model;
        else ProviderModels = new Dictionary<string, string>(ProviderModels) { [provider.ToString()] = model };
        if (provider == AssistantProvider) OnPropertyChanged(nameof(AssistantModel));
    }

    /// <summary>Base URL text of a provider. OpenAI's is shared with transcription.</summary>
    public string BaseUrlFor(AssistantProvider provider) =>
        provider == AssistantProvider.OpenAI ? ApiBaseUrl : ProviderBaseUrls.GetValueOrDefault(provider.ToString()) ?? provider.DefaultBaseUrl().AbsoluteUri;

    public void SetBaseUrl(AssistantProvider provider, string url)
    {
        if (provider == AssistantProvider.OpenAI) ApiBaseUrl = url;
        else ProviderBaseUrls = new Dictionary<string, string>(ProviderBaseUrls) { [provider.ToString()] = url };
        if (provider == AssistantProvider) OnPropertyChanged(nameof(AssistantBaseUrl));
    }

    /// <summary>The active assistant model.</summary>
    [JsonIgnore]
    public string AssistantModel { get => ModelFor(AssistantProvider); set => SetModel(AssistantProvider, value); }

    [JsonIgnore]
    public string AssistantBaseUrl { get => BaseUrlFor(AssistantProvider); set => SetBaseUrl(AssistantProvider, value); }

    /// <summary>Thread-safe network configuration of an assistant provider.</summary>
    public OpenAIConfiguration ConfigurationFor(AssistantProvider provider) =>
        provider == AssistantProvider.OpenAI ? NetworkConfiguration : OpenAIConfiguration.For(provider, BaseUrlFor(provider));

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
