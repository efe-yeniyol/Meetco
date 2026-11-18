using Meetco.Core.Models;

namespace Meetco.Core.Audio;

/// <summary>
/// A running audio source that delivers 16 kHz mono float32 samples.
/// Implemented per platform (WASAPI loopback / capture on Windows).
/// </summary>
public interface IAudioCapture : IDisposable
{
    AudioSourceKind Kind { get; }

    /// <summary>
    /// Starts capturing. <paramref name="onSamples"/> is raised on a background thread.
    /// <paramref name="onStopped"/> is raised once if capture stops on its own (device removed,
    /// format change), with the error if there was one. Throws when capture cannot start.
    /// </summary>
    void Start(Action<float[]> onSamples, Action<Exception?> onStopped);

    void Stop();
}

/// <summary>Creates platform audio sources and reports permission state.</summary>
public interface IAudioCaptureFactory
{
    IAudioCapture CreateSystemCapture();

    /// <summary>Creates a microphone capture for a device id, or the default device when null.</summary>
    IAudioCapture CreateMicrophoneCapture(string? deviceId);

    /// <summary>
    /// True when the operating system lets this app use the microphone.
    /// Returns a user-facing explanation when it does not.
    /// </summary>
    bool CanUseMicrophone(out string? problem);
}
