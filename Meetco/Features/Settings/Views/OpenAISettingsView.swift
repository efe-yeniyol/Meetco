import SwiftUI

struct OpenAISettingsView: View {
    @Environment(SettingsStore.self) private var settings
    @State private var keyModel: APIKeyViewModel

    init(dependencies: SettingsDependencies) {
        _keyModel = State(initialValue: APIKeyViewModel(keyProvider: dependencies.keyProvider, client: dependencies.client))
    }

    var body: some View {
        @Bindable var settings = settings
        Form {
            Section("API Key") {
                APIKeyEditor(model: keyModel)
                BYOKExplanation()
            }

            Section("Models") {
                ModelPicker(title: "Assistant model", selection: $settings.chatModel, presets: ModelCatalog.assistantModels, available: keyModel.availableModels)
                ModelPicker(title: "Transcription model", selection: $settings.transcriptionModel, presets: ModelCatalog.transcriptionModels, available: [])
            }

            Section("Responses") {
                Picker("Response language", selection: $settings.responseLanguage) {
                    ForEach(ResponseLanguage.allCases) { Text($0.title).tag($0) }
                }
                Picker("Explanation language", selection: $settings.explanationLanguage) {
                    ForEach(ExplanationLanguage.allCases) { Text($0.title).tag($0) }
                }
                Picker("Response style", selection: $settings.responseStyle) {
                    ForEach(ResponseStyle.allCases) { Text($0.title).tag($0) }
                }
                Toggle("Suggest answers automatically when a question is detected", isOn: $settings.autoSuggest)
            }

            Section {
                TextEditor(text: $settings.userContext)
                    .font(.body)
                    .frame(minHeight: 70)
                    .scrollContentBackground(.hidden)
            } header: {
                Text("About you (optional)")
            } footer: {
                Text("Your role, company, products and facts the assistant may use, e.g. \"I'm a solutions engineer at Acme. Our platform supports SSO and EU hosting.\" Meetco will not invent facts that are not here or in the conversation.")
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

/// Picker with presets plus a free-form model id.
struct ModelPicker: View {
    let title: String
    @Binding var selection: String
    let presets: [ModelCatalog.Entry]
    let available: [String]

    @State private var customValue = ""

    private var isPreset: Bool { presets.contains { $0.id == selection } }

    var body: some View {
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
                TextField("Model id, e.g. gpt-4.1-mini", text: $selection)
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
