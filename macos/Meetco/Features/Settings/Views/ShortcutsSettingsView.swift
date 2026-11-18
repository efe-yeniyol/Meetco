import SwiftUI

struct ShortcutsSettingsView: View {
    @Environment(SettingsStore.self) private var settings

    var body: some View {
        @Bindable var settings = settings
        Form {
            Section {
                Toggle("Enable global keyboard shortcuts", isOn: $settings.globalShortcutsEnabled)
            } footer: {
                Text("Global shortcuts work while Google Meet or Microsoft Teams is in front, without switching apps.")
                    .foregroundStyle(.secondary)
            }
            Section("Shortcuts") {
                ForEach(ShortcutAction.allCases) { action in
                    LabeledContent(action.title) {
                        Text(action.displayString)
                            .font(.system(.body, design: .rounded).weight(.medium))
                            .padding(.horizontal, 8)
                            .padding(.vertical, 2)
                            .background(RoundedRectangle(cornerRadius: 5).fill(Theme.subtleFill))
                            .overlay(RoundedRectangle(cornerRadius: 5).strokeBorder(Theme.hairline))
                    }
                    .opacity(settings.globalShortcutsEnabled ? 1 : 0.5)
                }
            }
        }
        .formStyle(.grouped)
    }
}
