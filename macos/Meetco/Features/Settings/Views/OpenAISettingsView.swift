import SwiftUI

/// OpenAI account used for transcription (and for answers when OpenAI is the assistant provider).
struct OpenAISettingsView: View {
    @Environment(SettingsStore.self) private var settings
    @State private var keyModel: APIKeyViewModel

    init(dependencies: SettingsDependencies) {
        _keyModel = State(initialValue: dependencies.keyModel(for: .openAI))
    }

    var body: some View {
        @Bindable var settings = settings
        Form {
            Section("API Key") {
                APIKeyEditor(model: keyModel)
                BYOKExplanation()
            }

            Section {
                ModelPicker(title: "Transcription model", selection: $settings.transcriptionModel, presets: ModelCatalog.transcriptionModels, available: [])
            } header: {
                Text("Transcription")
            } footer: {
                Text("Used when Settings → Audio transcribes with OpenAI. Choose who writes the answers in the Assistant tab.")
                    .foregroundStyle(.secondary)
            }

            Section {
                TextField("API base URL", text: $settings.apiBaseURL)
                    .font(.system(.body, design: .monospaced))
                if OpenAIConfiguration.validatedBaseURL(settings.apiBaseURL) == nil {
                    Text("Invalid URL — the default \(OpenAIConfiguration.defaultBaseURL.absoluteString) will be used.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
                Button("Reset to Default") { settings.apiBaseURL = OpenAIConfiguration.defaultBaseURL.absoluteString }
            } header: {
                Text("Advanced")
            } footer: {
                Text("Only change this for an OpenAI-compatible proxy you trust. Your API key is sent to this address.")
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}

/// Picker with presets plus a free-form model id. Without presets (local servers), it offers
/// the models the server reported instead.
struct ModelPicker: View {
    let title: String
    @Binding var selection: String
    let presets: [ModelCatalog.Entry]
    let available: [String]

    @State private var customValue = ""

    private var isPreset: Bool { presets.contains { $0.id == selection } }

    var body: some View {
        if presets.isEmpty {
            serverModels
        } else {
            presetPicker
        }
    }

    private var serverModels: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                TextField(title, text: $selection, prompt: Text("Model id"))
                    .font(.system(.body, design: .monospaced))
                if !available.isEmpty {
                    Menu("Choose") {
                        ForEach(available, id: \.self) { model in
                            Button(model) { selection = model }
                        }
                    }
                    .fixedSize()
                }
            }
            if available.isEmpty {
                Text("Press Test Connection to load the models available on the server.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            } else if !selection.isEmpty && !available.contains(selection) {
                Text("This model id was not found on the server.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var presetPicker: some View {
        VStack(alignment: .leading, spacing: 6) {
            Picker(title, selection: Binding(
                get: { isPreset ? selection : "__custom" },
                set: { value in
                    if value == "__custom" {
                        customValue = isPreset ? "" : selection
                        if !customValue.isEmpty { selection = customValue }
                    } else {
                        selection = value
                    }
                }
            )) {
                ForEach(presets) { entry in
                    Text("\(entry.id) — \(entry.note)").tag(entry.id)
                }
                Divider()
                Text("Custom…").tag("__custom")
            }
            if !isPreset || selection.isEmpty {
                TextField("Model id", text: $selection)
                    .font(.system(.body, design: .monospaced))
                if !available.isEmpty && !available.contains(selection) {
                    Text("This model id was not found in your account's model list.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
            }
        }
    }
}
