@preconcurrency import AVFoundation
@preconcurrency import ScreenCaptureKit
import CoreMedia

enum SystemAudioCaptureError: LocalizedError {
    case permissionDenied
    case noDisplay
    case failed(String)

    var errorDescription: String? {
        switch self {
        case .permissionDenied:
            return "Screen & System Audio Recording permission is required to hear the meeting. Enable Meetco in System Settings → Privacy & Security → Screen & System Audio Recording, then quit and reopen Meetco (macOS applies the permission after a relaunch)."
        case .noDisplay:
            return "No display is available for system audio capture."
        case .failed(let message):
            return "System audio capture failed: \(message)"
        }
    }
}

/// Captures the Mac's audio output (Google Meet, Teams, …) with ScreenCaptureKit.
/// Meetco's own audio is excluded. No video is kept; frames are requested at 1 fps and ignored.
final class SystemAudioCaptureService: NSObject, @unchecked Sendable {
    typealias InterruptionHandler = @Sendable (Error) -> Void

    private let lock = NSLock()
    private var stream: SCStream?
    private var continuation: AsyncStream<[Float]>.Continuation?
    private var interruptionHandler: InterruptionHandler?

    private let audioQueue = DispatchQueue(label: "io.github.meetco.system-audio", qos: .userInitiated)
    private let videoQueue = DispatchQueue(label: "io.github.meetco.system-video", qos: .background)
    private let resampler = PCMResampler()

    /// Starts capturing and returns a stream of 16 kHz mono samples.
    func start(onInterruption: @escaping InterruptionHandler) async throws -> AsyncStream<[Float]> {
        await stop()

        let content: SCShareableContent
        do {
            content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        } catch {
            Log.audio.error("SCShareableContent failed: \(error.localizedDescription, privacy: .public)")
            throw SystemAudioCaptureError.permissionDenied
        }

        let mainID = CGMainDisplayID()
        guard let display = content.displays.first(where: { $0.displayID == mainID }) ?? content.displays.first else {
            throw SystemAudioCaptureError.noDisplay
        }

        let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])
        let configuration = SCStreamConfiguration()
        configuration.capturesAudio = true
        configuration.excludesCurrentProcessAudio = true
        configuration.sampleRate = 48_000
        configuration.channelCount = 2
        // Audio-only use: keep the mandatory video stream as cheap as possible.
        configuration.width = 2
        configuration.height = 2
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: 1)
        configuration.showsCursor = false
        configuration.queueDepth = 3

        let (audioStream, continuation) = AsyncStream.makeStream(of: [Float].self, bufferingPolicy: .bufferingNewest(512))
        let scStream = SCStream(filter: filter, configuration: configuration, delegate: self)
        do {
            try scStream.addStreamOutput(self, type: .audio, sampleHandlerQueue: audioQueue)
            try scStream.addStreamOutput(self, type: .screen, sampleHandlerQueue: videoQueue)
        } catch {
            continuation.finish()
            throw SystemAudioCaptureError.failed(error.localizedDescription)
        }

        lock.withLock {
            self.stream = scStream
            self.continuation = continuation
            self.interruptionHandler = onInterruption
        }

        do {
            try await scStream.startCapture()
        } catch {
            lock.withLock {
                self.stream = nil
                self.continuation = nil
                self.interruptionHandler = nil
            }
            continuation.finish()
            Log.audio.error("startCapture failed: \(error.localizedDescription, privacy: .public)")
            if (error as NSError).code == SCStreamError.userDeclined.rawValue {
                throw SystemAudioCaptureError.permissionDenied
            }
            throw SystemAudioCaptureError.failed(error.localizedDescription)
        }
        Log.audio.info("System audio capture started")
        return audioStream
    }

    func stop() async {
        let (stream, continuation) = lock.withLock { () -> (SCStream?, AsyncStream<[Float]>.Continuation?) in
            let result = (self.stream, self.continuation)
            self.stream = nil
            self.continuation = nil
            self.interruptionHandler = nil
            return result
        }
        continuation?.finish()
        if let stream {
            do {
                try await stream.stopCapture()
            } catch {
                Log.audio.debug("stopCapture: \(error.localizedDescription, privacy: .public)")
            }
            Log.audio.info("System audio capture stopped")
        }
    }

    // MARK: Sample conversion

    static func pcmBuffer(from sampleBuffer: CMSampleBuffer) -> AVAudioPCMBuffer? {
        guard sampleBuffer.isValid,
              let description = sampleBuffer.formatDescription,
              var asbd = description.audioStreamBasicDescription,
              let format = AVAudioFormat(streamDescription: &asbd) else { return nil }
        let frames = AVAudioFrameCount(sampleBuffer.numSamples)
        guard frames > 0, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames) else { return nil }
        buffer.frameLength = frames
        let status = CMSampleBufferCopyPCMDataIntoAudioBufferList(
            sampleBuffer,
            at: 0,
            frameCount: Int32(frames),
            into: buffer.mutableAudioBufferList
        )
        return status == noErr ? buffer : nil
    }
}

extension SystemAudioCaptureService: SCStreamOutput, SCStreamDelegate {
    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio, let buffer = Self.pcmBuffer(from: sampleBuffer) else { return }
        let samples = resampler.convert(buffer)
        guard !samples.isEmpty else { return }
        let continuation = lock.withLock { self.continuation }
        continuation?.yield(samples)
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        Log.audio.error("System audio stream stopped: \(error.localizedDescription, privacy: .public)")
        let (handler, continuation) = lock.withLock { () -> (InterruptionHandler?, AsyncStream<[Float]>.Continuation?) in
            let result = (self.interruptionHandler, self.continuation)
            self.stream = nil
            self.continuation = nil
            self.interruptionHandler = nil
            return result
        }
        continuation?.finish()
        handler?(error)
    }
}
