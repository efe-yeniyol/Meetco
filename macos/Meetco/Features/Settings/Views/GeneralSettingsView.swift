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
                LabeledContent("Glass color") {
                    GlassTintPicker(selection: $settings.glassTint)
                }
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

/// Row of colour swatches for the floating panels' glass tint.
private struct GlassTintPicker: View {
    @Binding var selection: GlassTint

    var body: some View {
        HStack(spacing: 8) {
            ForEach(GlassTint.allCases) { tint in
                Button { selection = tint } label: {
                    swatch(for: tint)
                        .frame(width: 18, height: 18)
                        .overlay(Circle().strokeBorder(Color.primary.opacity(0.2), lineWidth: 0.5))
                        .padding(3)
                        .overlay(Circle().strokeBorder(Color.accentColor, lineWidth: selection == tint ? 2 : 0))
                        .contentShape(Circle())
                }
                .buttonStyle(.plain)
                .help(tint.title)
                .accessibilityLabel(tint.title)
                .accessibilityAddTraits(selection == tint ? .isSelected : [])
            }
        }
    }

    @ViewBuilder
    private func swatch(for tint: GlassTint) -> some View {
        if tint == .neutral {
            Circle().fill(LinearGradient(stops: [
                .init(color: .black, location: 0.5), .init(color: .white, location: 0.5),
            ], startPoint: .topLeading, endPoint: .bottomTrailing))
        } else {
            Circle().fill(Color(nsColor: GlassContainer.swatchColor(for: tint)))
        }
    }
}
