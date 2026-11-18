using Meetco.Core.Infrastructure;

namespace Meetco.Core.Settings;

/// <summary>Stores the user's OpenAI API key securely (Windows Credential Manager in the app).</summary>
public interface IApiKeyStore
{
    string? Load();
    void Save(string key);
    void Delete();
}

/// <summary>Thread-safe, in-memory cache in front of the secure store so the key is read once per launch.</summary>
public sealed class ApiKeyProvider
{
    private readonly IApiKeyStore _store;
    private readonly object _gate = new();
    private string? _cached;
    private bool _loaded;

    public ApiKeyProvider(IApiKeyStore store)
    {
        _store = store;
    }

    /// <summary>Raised after the key was saved or deleted.</summary>
    public event Action? Changed;

    /// <summary>Returns the configured key, or null. Never logs the value.</summary>
    public string? CurrentKey()
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                try
                {
                    _cached = _store.Load();
                }
                catch (Exception error)
                {
                    Log.Error("storage", "Unable to read API key: " + error.Message);
                    _cached = null;
                }
                _loaded = true;
            }
            return _cached;
        }
    }

    public bool HasKey => !string.IsNullOrEmpty(CurrentKey());

    public void Save(string key)
    {
        var trimmed = key.Trim();
        _store.Save(trimmed);
        lock (_gate)
        {
            _cached = trimmed;
            _loaded = true;
        }
        Changed?.Invoke();
    }

    public void Delete()
    {
        _store.Delete();
        lock (_gate)
        {
            _cached = null;
            _loaded = true;
        }
        Changed?.Invoke();
    }

    /// <summary>Masked form for display, e.g. "sk-…a1B2".</summary>
    public static string Mask(string key) =>
        key.Length <= 8 ? new string('•', key.Length) : key[..3] + "…" + key[^4..];
}
