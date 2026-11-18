import SwiftUI

/// The small, always-on-top glass toolbar.
struct ControlBarView: View {
    @Environment(AppState.self) private var appState
    @Environment(MeetingSessionController.self) private var session
    @Environment(SuggestionEngine.self) private var suggestions
    @Environment(SettingsStore.self) private var settings
    @Environment(\.appActions) private var actions

    var body: some View {
        HStack(spacing: 2) {
            Image(systemName: "waveform")
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(.secondary)
                .frame(width: 22)
                .help("Meetco — drag to move")

            ListenButton()

            IconButton(
                systemName: session.isMicrophoneMuted ? "mic.slash" : "mic",
                help: session.isMicrophoneMuted ? "Unmute microphone" : "Mute microphone",
                isActive: session.isMicrophoneMuted
            ) {
                session.isMicrophoneMuted.toggle()
            }
            .disabled(!settings.captureMode.includesMicrophone)

            divider

            IconButton(systemName: "quote.bubble", help: "What should I say? (⌃⌥A)") {
                appState.show(.answers)
                suggestions.whatShouldISay()
            }

            IconButton(
                systemName: suggestions.pendingScreenContext == nil ? "camera.viewfinder" : "photo.badge.checkmark",
                help: "Capture screen as context for the next answer",
                isActive: suggestions.pendingScreenContext != nil
            ) {
                suggestions.captureScreenContext()
                appState.show(.answers)
            }
            .disabled(suggestions.isCapturingScreen)

            divider

            IconButton(
                systemName: appState.isPanelVisible ? "eye.slash" : "eye",
                help: appState.isPanelVisible ? "Hide panel (⌃⌥M)" : "Show panel (⌃⌥M)"
            ) {
                appState.togglePanel()
            }

            IconButton(systemName: "gearshape", help: "Settings") {
                actions.openSettings()
            }
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .fixedSize()
    }

    private var divider: some View {
        Rectangle().fill(Theme.hairline).frame(width: 1, height: 18).padding(.horizontal, 4)
    }
}

/// Start/stop capsule showing the listening state and elapsed time.
struct ListenButton: View {
    @Environment(MeetingSessionController.self) private var session
    @Environment(SettingsStore.self) private var settings
    @State private var isHovering = false

    var body: some View {
        Button {
            session.toggleListening()
        } label: {
            HStack(spacing: 6) {
                if session.isBusy {
                    ProgressView().controlSize(.mini)
                } else if session.isListening && isHovering {
                    Image(systemName: "stop.fill").font(.system(size: 8)).foregroundStyle(Theme.recording)
                } else {
                    RecordingDot(isActive: session.isListening)
                }
                Group {
                    if session.isListening, let start = session.startedAt {
                        ElapsedTimeText(start: start)
                    } else {
                        Text(session.state == .starting ? "Starting" : session.state == .stopping ? "Stopping" : "Listen")
                    }
                }
                .font(.system(size: 12, weight: .medium))
                .frame(minWidth: 44, alignment: .leading)
            }
            .padding(.horizontal, 10)
            .frame(height: 28)
            .background(Capsule(style: .continuous).fill(isHovering ? Theme.hoverFill : Theme.subtleFill))
            .overlay(Capsule(style: .continuous).strokeBorder(Theme.hairline, lineWidth: 0.5))
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .onHover { isHovering = $0 }
        .help(session.isListening ? "Stop listening (⌃⌥L)" : "Start listening (⌃⌥L) — right-click to choose audio source")
        .disabled(session.isBusy)
        .contextMenu {
            Picker("Audio source", selection: Binding(get: { settings.captureMode }, set: { session.setCaptureMode($0) })) {
                ForEach(AudioCaptureMode.allCases) { Text($0.title).tag($0) }
            }
            .pickerStyle(.inline)
        }
    }
}
