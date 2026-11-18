@preconcurrency import AVFoundation
import CoreAudio

enum MicrophoneCaptureError: LocalizedError {
    case permissionDenied
    case noInputDevice
    case engineFailed(String)

    var errorDescription: String? {
        switch self {
        case .permissionDenied:
            return "Microphone access is required. Enable Meetco in System Settings → Privacy & Security → Microphone."
        case .noInputDevice:
            return "No microphone input device is available."
        case .engineFailed(let message):
            return "Microphone capture failed: \(message)"
        }
    }
}

/// Captures the user's microphone with AVAudioEngine and recovers from device changes
/// (e.g. connecting AirPods mid-meeting).
final class MicrophoneCaptureService: @unchecked Sendable {
    private let lock = NSLock()
    private var engine: AVAudioEngine?
    private var continuation: AsyncStream<[Float]>.Continuation?
    private var configurationObserver: NSObjectProtocol?
    private var deviceUID: String?
    private let restartQueue = DispatchQueue(label: "io.github.meetco.microphone-restart")

    /// Starts capture and returns a stream of 16 kHz mono samples.
    /// - Parameter deviceUID: Core Audio UID of the input device, or nil for the system default.
    func start(deviceUID: String?) throws -> AsyncStream<[Float]> {
        stop()
        let (stream, continuation) = AsyncStream.makeStream(of: [Float].self, bufferingPolicy: .bufferingNewest(512))
        lock.withLock {
            self.continuation = continuation
            self.deviceUID = deviceUID
        }
        do {
            try startEngine()
        } catch {
            stop()
            throw error
        }
        Log.audio.info("Microphone capture started")
        return stream
    }

    func stop() {
        teardownEngine()
        let continuation = lock.withLock { () -> AsyncStream<[Float]>.Continuation? in
            let current = self.continuation
            self.continuation = nil
            return current
        }
        if continuation != nil { Log.audio.info("Microphone capture stopped") }
        continuation?.finish()
    }

    // MARK: Engine lifecycle

    private func startEngine() throws {
        let (continuation, deviceUID) = lock.withLock { (self.continuation, self.deviceUID) }
        guard let continuation else { return }

        let engine = AVAudioEngine()
        let input = engine.inputNode

        if let deviceUID, let device = AudioDeviceManager.inputDevice(withUID: deviceUID) {
            if !AudioDeviceManager.setInputDevice(device.deviceID, on: input) {
                Log.audio.error("Could not select input device; using the system default")
            }
        }

        let format = input.outputFormat(forBus: 0)
        guard format.sampleRate > 0, format.channelCount > 0 else {
            throw MicrophoneCaptureError.noInputDevice
        }

        let resampler = PCMResampler()
        input.installTap(onBus: 0, bufferSize: 2048, format: format) { buffer, _ in
            let samples = resampler.convert(buffer)
            if !samples.isEmpty { continuation.yield(samples) }
        }

        engine.prepare()
        do {
            try engine.start()
        } catch {
            input.removeTap(onBus: 0)
            throw MicrophoneCaptureError.engineFailed(error.localizedDescription)
        }

        let observer = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange,
            object: engine,
            queue: nil
        ) { [weak self] _ in
            self?.scheduleRestart()
        }

        lock.withLock {
            self.engine = engine
            self.configurationObserver = observer
        }
    }

    private func scheduleRestart() {
        restartQueue.asyncAfter(deadline: .now() + 0.3) { [weak self] in
            guard let self else { return }
            let isActive = self.lock.withLock { self.continuation != nil }
            guard isActive else { return }
            Log.audio.notice("Audio configuration changed; restarting microphone")
            self.teardownEngine()
            do {
                try self.startEngine()
            } catch {
                Log.audio.error("Microphone restart failed: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    private func teardownEngine() {
        let (engine, observer) = lock.withLock { () -> (AVAudioEngine?, NSObjectProtocol?) in
            let result = (self.engine, self.configurationObserver)
            self.engine = nil
            self.configurationObserver = nil
            return result
        }
        if let observer { NotificationCenter.default.removeObserver(observer) }
        if let engine {
            engine.inputNode.removeTap(onBus: 0)
            engine.stop()
        }
    }
}
