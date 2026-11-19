import SwiftUI

/// Native Settings window with tabs.
struct SettingsView: View {
    @Environment(\.settingsDependencies) private var dependencies
    @Environment(SettingsStore.self) private var settings

    var body: some View {
        TabView {
            Group {
                if let dependencies {
                    OpenAISettingsView(dependencies: dependencies)
                }
            }
            .tabItem { Label("OpenAI", systemImage: "key") }

            Group {
                if let dependencies {
                    AssistantSettingsView(dependencies: dependencies)
                }
            }
            .tabItem { Label("Assistant", systemImage: "sparkles") }

            GeneralSettingsView()
                .tabItem { Label("General", systemImage: "gearshape") }

            AudioSettingsView()
                .tabItem { Label("Audio", systemImage: "waveform") }

            ShortcutsSettingsView()
                .tabItem { Label("Shortcuts", systemImage: "command") }

            Group {
                if let dependencies {
                    PrivacySettingsView(archive: dependencies.archive)
                }
            }
            .tabItem { Label("Privacy", systemImage: "hand.raised") }
        }
        .scrollContentBackground(settings.glassTint == .neutral ? .automatic : .hidden)
        .background(TintedWindowBackground())
        .frame(width: 600, height: 560)
    }
}
