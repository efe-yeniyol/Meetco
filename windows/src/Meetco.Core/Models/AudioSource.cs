using System.Text.Json.Serialization;

namespace Meetco.Core.Models;

/// <summary>Where a piece of audio (and therefore a transcript segment) came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AudioSourceKind>))]
public enum AudioSourceKind
{
    /// <summary>Audio played by the computer (other meeting participants), captured with WASAPI loopback.</summary>
    System,
    /// <summary>The user's microphone.</summary>
    Microphone,
}

/// <summary>Which audio sources a listening session captures.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AudioCaptureMode>))]
public enum AudioCaptureMode
{
    SystemAndMicrophone,
    SystemOnly,
    MicrophoneOnly,
}

public static class AudioSourceExtensions
{
    public static string SpeakerLabel(this AudioSourceKind source) => source switch
    {
        AudioSourceKind.System => "Participants",
        _ => "You",
    };

    /// <summary>Label used inside prompts sent to the model.</summary>
    public static string PromptLabel(this AudioSourceKind source) => source switch
    {
        AudioSourceKind.System => "Participant",
        _ => "You",
    };

    public static string Title(this AudioCaptureMode mode) => mode switch
    {
        AudioCaptureMode.SystemAndMicrophone => "Microphone + System Audio",
        AudioCaptureMode.SystemOnly => "System Audio Only",
        _ => "Microphone Only",
    };

    public static string ShortTitle(this AudioCaptureMode mode) => mode switch
    {
        AudioCaptureMode.SystemAndMicrophone => "Mic + System",
        AudioCaptureMode.SystemOnly => "System",
        _ => "Mic",
    };

    public static bool IncludesSystemAudio(this AudioCaptureMode mode) => mode != AudioCaptureMode.MicrophoneOnly;
    public static bool IncludesMicrophone(this AudioCaptureMode mode) => mode != AudioCaptureMode.SystemOnly;
}
