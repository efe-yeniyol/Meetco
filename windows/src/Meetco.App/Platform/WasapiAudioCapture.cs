using System.IO;
using System.Runtime.InteropServices;
using Meetco.Core.Audio;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace MeetcoApp.Platform;

/// <summary>An input device the user can pick in Settings.</summary>
public sealed record MicrophoneDevice(string Id, string Name);

/// <summary>Creates WASAPI captures: loopback of the default output device and a microphone.</summary>
public sealed class WasapiAudioCaptureFactory : IAudioCaptureFactory
{
    public const string MicrophonePrivacyHint =
        "Windows is blocking microphone access. Open Settings → Privacy & security → Microphone and turn on " +
        "\"Microphone access\" and \"Let desktop apps access your microphone\", then try again.";

    public IAudioCapture CreateSystemCapture() => new WasapiAudioCapture(AudioSourceKind.System, null);

    public IAudioCapture CreateMicrophoneCapture(string? deviceId) => new WasapiAudioCapture(AudioSourceKind.Microphone, deviceId);

    public bool CanUseMicrophone(out string? problem)
    {
        problem = null;
        if (IsMicrophoneBlockedByPrivacySettings())
        {
            problem = MicrophonePrivacyHint;
            return false;
        }
        if (ListMicrophones().Count == 0)
        {
            problem = "No microphone was found. Connect a microphone or switch to \"System audio only\".";
            return false;
        }
        return true;
    }

    public static List<MicrophoneDevice> ListMicrophones()
    {
        var devices = new List<MicrophoneDevice>();
        try
        {
            var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                devices.Add(new MicrophoneDevice(device.ID, device.FriendlyName));
            }
        }
        catch (COMException error)
        {
            Log.Error("audio", "Could not list microphones: " + error.Message);
        }
        return devices;
    }

    /// <summary>Reads the Windows microphone privacy switches (global and for desktop apps).</summary>
    private static bool IsMicrophoneBlockedByPrivacySettings()
    {
        const string consentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        try
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(consentStore);
                if (key?.GetValue("Value") is string value && value.Equals("Deny", StringComparison.OrdinalIgnoreCase)) return true;
            }
            using var desktopApps = Registry.CurrentUser.OpenSubKey(consentStore + @"\NonPackaged");
            if (desktopApps?.GetValue("Value") is string desktop && desktop.Equals("Deny", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unknown: let the capture itself report a problem.
        }
        return false;
    }
}

/// <summary>
/// One WASAPI capture (shared mode). System audio uses loopback on the default output device,
/// so Meetco hears exactly what the meeting app plays; the microphone uses the chosen or the
/// default communications input. Delivers 16 kHz mono float32.
/// </summary>
public sealed class WasapiAudioCapture : IAudioCapture, IMMNotificationClient
{
    private readonly string? _deviceId;
    private readonly object _gate = new();
    private IWaveIn? _capture;
    private MMDeviceEnumerator? _enumerator;
    private AudioFormatConverter? _converter;
    private Action<float[]>? _onSamples;
    private Action<Exception?>? _onStopped;
    private bool _stoppedByUs;
    private bool _reportedStop;
    private bool _notificationsRegistered;

    public WasapiAudioCapture(AudioSourceKind kind, string? deviceId)
    {
        Kind = kind;
        _deviceId = deviceId;
    }

    public AudioSourceKind Kind { get; }

    public void Start(Action<float[]> onSamples, Action<Exception?> onStopped)
    {
        _onSamples = onSamples;
        _onStopped = onStopped;
        try
        {
            _enumerator = new MMDeviceEnumerator();
            IWaveIn capture;
            if (Kind == AudioSourceKind.System)
            {
                capture = new WasapiLoopbackCapture();
            }
            else
            {
                var device = FindMicrophone(_enumerator, _deviceId)
                    ?? throw new InvalidOperationException("No microphone was found.");
                capture = new WasapiCapture(device, false, 100);
            }

            _converter = CreateConverter(capture.WaveFormat);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            _capture = capture;
            capture.StartRecording();
            Log.Info("audio", $"{Kind} capture started ({capture.WaveFormat.SampleRate} Hz, {capture.WaveFormat.Channels} ch, {capture.WaveFormat.BitsPerSample} bit)");
        }
        catch (Exception error) when (IsAccessDenied(error))
        {
            Dispose();
            throw new InvalidOperationException(Kind == AudioSourceKind.Microphone
                ? WasapiAudioCaptureFactory.MicrophonePrivacyHint
                : "Windows denied access to the system audio output.", error);
        }
        catch (Exception error) when (error is COMException or ArgumentException or NotSupportedException)
        {
            Dispose();
            throw new InvalidOperationException(
                (Kind == AudioSourceKind.Microphone ? "The microphone could not be started: " : "System audio could not be captured: ") + error.Message,
                error);
        }

        if (Kind == AudioSourceKind.System)
        {
            // Loopback keeps listening to the old device when the default output changes
            // (e.g. headphones plugged in): report it so the session restarts capture.
            try
            {
                _enumerator.RegisterEndpointNotificationCallback(this);
                _notificationsRegistered = true;
            }
            catch (Exception error)
            {
                Log.Notice("audio", "Device change notifications unavailable: " + error.Message);
            }
        }
    }

    public void Stop()
    {
        IWaveIn? capture;
        lock (_gate)
        {
            _stoppedByUs = true;
            capture = _capture;
        }
        try
        {
            capture?.StopRecording();
        }
        catch (Exception error)
        {
            Log.Notice("audio", $"{Kind} capture stop: {error.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate) _stoppedByUs = true;
        if (_notificationsRegistered && _enumerator is not null)
        {
            try
            {
                _enumerator.UnregisterEndpointNotificationCallback(this);
            }
            catch
            {
                // Ignore: the enumerator is going away anyway.
            }
            _notificationsRegistered = false;
        }
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try
            {
                capture.Dispose();
            }
            catch (Exception error)
            {
                Log.Notice("audio", $"{Kind} capture dispose: {error.Message}");
            }
        }
        _enumerator = null;
    }

    // MARK: Capture callbacks

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _converter is null) return;
        lock (_gate)
        {
            if (_stoppedByUs) return;
        }
        float[] samples;
        try
        {
            samples = _converter.Convert(e.Buffer, e.BytesRecorded);
        }
        catch (Exception error)
        {
            Log.Error("audio", "Audio conversion failed: " + error.Message);
            return;
        }
        if (samples.Length == 0) return;
        try
        {
            _onSamples?.Invoke(samples);
        }
        catch (Exception error)
        {
            // Never let a processing bug look like a device failure (NAudio would stop recording).
            Log.Error("audio", "Processing captured audio failed: " + error.Message);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) Log.Error("audio", $"{Kind} capture stopped: {e.Exception.Message}");
        ReportStopped(e.Exception);
    }

    private void ReportStopped(Exception? error)
    {
        lock (_gate)
        {
            if (_stoppedByUs || _reportedStop) return;
            _reportedStop = true;
        }
        _onStopped?.Invoke(error);
    }

    // MARK: Format handling

    private static AudioFormatConverter CreateConverter(WaveFormat format)
    {
        SampleEncoding encoding;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible extensible && extensible.SubFormat == FloatSubFormat);
        if (format.BitsPerSample == 32 && (isFloat || format.Encoding == WaveFormatEncoding.Extensible && !IsPcmExtensible(format))) encoding = SampleEncoding.Float32;
        else if (format.BitsPerSample == 32) encoding = SampleEncoding.Pcm32;
        else if (format.BitsPerSample == 24) encoding = SampleEncoding.Pcm24;
        else if (format.BitsPerSample == 16) encoding = SampleEncoding.Pcm16;
        else throw new NotSupportedException($"Unsupported audio format: {format.BitsPerSample}-bit {format.Encoding}.");
        return new AudioFormatConverter(encoding, format.Channels, format.SampleRate);
    }

    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    private static bool IsPcmExtensible(WaveFormat format) =>
        format is WaveFormatExtensible extensible && extensible.SubFormat == PcmSubFormat;

    private static MMDevice? FindMicrophone(MMDeviceEnumerator enumerator, string? deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                var device = enumerator.GetDevice(deviceId);
                if (device.State == DeviceState.Active) return device;
            }
            catch (COMException)
            {
                // The saved device is gone: fall back to the default one.
            }
            Log.Notice("audio", "Selected microphone not available; using the default microphone");
        }
        // Console first: opening the Communications device can make Windows duck other audio
        // ("reduce other sounds by 80%"), which would turn the meeting down.
        foreach (var role in new[] { Role.Console, Role.Communications })
        {
            try
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
            }
            catch (COMException)
            {
            }
        }
        return null;
    }

    private static bool IsAccessDenied(Exception error) =>
        error is UnauthorizedAccessException
        || (error is COMException com && (uint)com.HResult == 0x80070005);

    // MARK: IMMNotificationClient (called on a COM thread)

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
    }

    public void OnDeviceRemoved(string deviceId)
    {
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Render || role != Role.Console) return;
        Log.Notice("audio", "Default output device changed");
        ThreadPool.QueueUserWorkItem(_ => ReportStopped(new InvalidOperationException("The default output device changed.")));
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }
}
