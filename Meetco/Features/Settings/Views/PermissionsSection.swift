import SwiftUI

/// Shows macOS permission status with buttons to grant them.
struct PermissionsList: View {
    @Environment(PermissionsManager.self) private var permissions

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            PermissionRow(
                title: "Microphone",
                detail: "Transcribes your side of the conversation (Mic modes only).",
                systemImage: "mic",
                state: permissions.microphone,
                action: { permissions.requestMicrophone() }
            )
            PermissionRow(
                title: "Screen & System Audio Recording",
                detail: "Required to hear other participants in Meet/Teams. Meetco requests audio only and never records video or saves audio. Relaunch Meetco after granting.",
                systemImage: "speaker.wave.2",
                state: permissions.screenRecording,
                action: { permissions.requestScreenRecording() }
            )
            if permissions.screenRecording != .granted && permissions.screenRecordingNeedsRelaunch {
                HStack {
                    Text("Already enabled Meetco in System Settings? macOS applies it after a relaunch.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer()
                    Button("Relaunch Meetco") { permissions.relaunch() }
                }
            }
        }
        .onAppear { permissions.refresh() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            permissions.refresh()
        }
    }
}

struct PermissionRow: View {
    let title: String
    let detail: String
    let systemImage: String
    let state: PermissionState
    let action: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: systemImage)
                .font(.system(size: 14))
                .frame(width: 20)
                .foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(.body.weight(.medium))
                Text(detail)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            if state == .granted {
                Label("Granted", systemImage: "checkmark.circle")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            } else {
                Button(state == .denied ? "Open Settings" : "Grant…", action: action)
            }
        }
    }
}
