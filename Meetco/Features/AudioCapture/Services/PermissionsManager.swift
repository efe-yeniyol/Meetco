import AppKit
import AVFoundation
import CoreGraphics
import Observation

enum PermissionState: Equatable {
    case granted
    case denied
    case notDetermined

    var title: String {
        switch self {
        case .granted: "Granted"
        case .denied: "Not granted"
        case .notDetermined: "Not requested"
        }
    }
}

/// Tracks and requests the two macOS privacy permissions Meetco needs.
@MainActor
@Observable
final class PermissionsManager {
    private(set) var microphone: PermissionState = .notDetermined
    private(set) var screenRecording: PermissionState = .notDetermined
    /// macOS only applies a newly granted Screen Recording permission after the app relaunches.
    private(set) var screenRecordingNeedsRelaunch = false

    init() {
        refresh()
    }

    func refresh() {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: microphone = .granted
        case .notDetermined: microphone = .notDetermined
        default: microphone = .denied
        }
        // macOS cannot distinguish "denied" from "not requested" for screen recording.
        screenRecording = CGPreflightScreenCaptureAccess() ? .granted : .notDetermined
    }

    /// Returns true when the microphone can be used, prompting if needed.
    func ensureMicrophone() async -> Bool {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            microphone = .granted
            return true
        case .notDetermined:
            let granted = await AVCaptureDevice.requestAccess(for: .audio)
            refresh()
            return granted
        default:
            microphone = .denied
            return false
        }
    }

    func requestMicrophone() {
        Task {
            if AVCaptureDevice.authorizationStatus(for: .audio) == .denied {
                openSystemSettings(for: .microphone)
            } else {
                _ = await ensureMicrophone()
            }
        }
    }

    /// Shows the system prompt the first time; afterwards opens System Settings.
    func requestScreenRecording() {
        if CGPreflightScreenCaptureAccess() {
            screenRecording = .granted
            return
        }
        // Only show the system prompt once; afterwards go straight to System Settings
        // so the user isn't asked again and again.
        let key = "permissions.screenRecordingRequested"
        if UserDefaults.standard.bool(forKey: key) {
            openSystemSettings(for: .screenRecording)
        } else {
            UserDefaults.standard.set(true, forKey: key)
            if !CGRequestScreenCaptureAccess() { openSystemSettings(for: .screenRecording) }
        }
        screenRecordingNeedsRelaunch = true
        refresh()
    }

    /// Quits and reopens Meetco so a newly granted permission takes effect.
    func relaunch() {
        let path = Bundle.main.bundleURL.path
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/bin/sh")
        task.arguments = ["-c", "sleep 1; /usr/bin/open \"$0\"", path]
        try? task.run()
        NSApp.terminate(nil)
    }

    enum Pane { case microphone, screenRecording }

    func openSystemSettings(for pane: Pane) {
        let anchor = pane == .microphone ? "Privacy_Microphone" : "Privacy_ScreenCapture"
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(anchor)") {
            NSWorkspace.shared.open(url)
        }
    }
}
