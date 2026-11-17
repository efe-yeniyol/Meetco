import SwiftUI

struct GeneralSettingsView: View {
    @Environment(SettingsStore.self) private var settings
    @Environment(AppState.self) private var appState

    var body: some View {
        @Bindable var settings = settings
        Form {
            Section("Appearance") {
                Picker("Appearance", selection: $settings.appearance) {
                    ForEach(AppearanceMode.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                Picker("Default panel size", selection: $settings.defaultPanelSize) {
                    ForEach(PanelSize.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
            }

            Section {
                Toggle("Keep Meetco above other windows", isOn: $settings.alwaysOnTop)
                Toggle("Show on all Spaces and over full-screen meetings", isOn: $settings.showOnAllSpaces)
                Toggle("Keep Meetco out of my screen shares when presenting", isOn: $settings.hideFromScreenSharing)
                HStack {
                    Button("Show Panel") { appState.isPanelVisible = true; appState.isControlBarVisible = true }
                    Button("Reset Window Positions") { resetWindowPositions() }
                }
            } header: {
                Text("Floating windows")
            } footer: {
                Text("Off by default. Keeps the panel from cluttering slides you share. This is best effort: some apps and recent macOS versions still capture all windows, so check before presenting.")
                    .foregroundStyle(.secondary)
            }

            Section("Transcript") {
                Toggle("Automatically scroll to the latest speech", isOn: $settings.autoScrollTranscript)
                Toggle("Start each listening session with a clean transcript", isOn: $settings.clearTranscriptOnNewSession)
            }
        }
        .formStyle(.grouped)
    }

    private func resetWindowPositions() {
        for name in ["MeetcoControlBar", "MeetcoMainPanel"] {
            UserDefaults.standard.removeObject(forKey: "NSWindow Frame \(name)")
        }
        UserDefaults.standard.removeObject(forKey: "window.expandedSize")
        UserDefaults.standard.removeObject(forKey: "window.compactSize")
    }
}
