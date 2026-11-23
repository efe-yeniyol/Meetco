import SwiftUI

/// Who writes the answers (provider, key, model) and how they are written.
struct AssistantSettingsView: View {
    @Environment(SettingsStore.self) private var settings
    let dependencies: SettingsDependencies

    var body: some View {
        @Bindable var settings = settings
        Form {
            Section {
                Picker("Provider", selection: $settings.assistantProvider) {
                    ForEach(AssistantProvider.allCases) { Text($0.title).tag($0) }
                }
            } header: {
                Text("Assistant provider")
            } footer: {
                Text(providerFooter(settings.assistantProvider))
                    .foregroundStyle(.secondary)
            }

            ProviderConnectionSection(provider: settings.assistantProvider, dependencies: dependencies)
                .id(settings.assistantProvider)

            Section {
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
                Toggle("Detect questions in any language", isOn: $settings.detectQuestionsInAnyLanguage)
                    .disabled(!settings.autoSuggest)
                Toggle("Meeting memory: remember people and what was said", isOn: $settings.meetingMemoryEnabled)
            } header: {
                Text("Responses")
            } footer: {
                Text("Meetco works in \(SpokenLanguage.all.count) languages and answers in the speaker's language. Questions are spotted instantly by their question mark, or by keywords in English and Turkish. With \"Detect questions in any language\" on, the assistant model also checks every other phrase from participants, in any language, and answers only real questions. This makes more requests to your provider but adds no delay.\n\nMeeting memory keeps track of who is in the meeting (names, roles, companies) and the facts, numbers and decisions mentioned, so answers stay consistent with the whole meeting and fit the person asking. It is updated in the background about every 30 seconds of conversation and shown in the panel's sidebar.")
                    .foregroundStyle(.secondary)
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
        }
        .formStyle(.grouped)
    }

    private func providerFooter(_ provider: AssistantProvider) -> String {
        switch provider {
        case .openAI: "Uses the same OpenAI key as transcription."
        case .anthropic: "Claude via Anthropic's API, billed to your Anthropic account."
        case .deepSeek: "Billed to your DeepSeek account. DeepSeek does not read screenshots, so screen context is skipped."
        case .ollama: "Runs on this Mac: answers never leave your computer. Install Ollama, pull a model (e.g. `ollama pull qwen3`) and keep it running. Pair it with on-device transcription (Settings → Audio) to keep everything local."
        case .lmStudio: "Runs on this Mac: answers never leave your computer. Start LM Studio's local server with a model loaded. Pair it with on-device transcription (Settings → Audio) to keep everything local."
        case .custom: "Any server that speaks the OpenAI Chat Completions API: OpenRouter, Groq, Mistral, vLLM, llama.cpp and others."
        }
    }
}

/// Key, address and model for one provider. Rebuilt when the provider changes.
private struct ProviderConnectionSection: View {
    @Environment(SettingsStore.self) private var settings
    let provider: AssistantProvider
    @State private var keyModel: APIKeyViewModel

    init(provider: AssistantProvider, dependencies: SettingsDependencies) {
        self.provider = provider
        _keyModel = State(initialValue: dependencies.keyModel(for: provider))
    }

    var body: some View {
        Section {
            if provider == .openAI {
                Text("Your OpenAI key is managed in the OpenAI tab.")
                    .foregroundStyle(.secondary)
            } else {
                APIKeyEditor(model: keyModel)
                if let url = provider.keyPageURL {
                    Link(provider.requiresAPIKey ? "Get a \(provider.shortName) API key" : "Download \(provider.shortName)", destination: url)
                        .font(.callout)
                }
            }
        } header: {
            Text(provider.requiresAPIKey ? "API key" : "Connection")
        }

        Section("Model") {
            ModelPicker(
                title: "Assistant model",
                selection: Binding(get: { settings.model(for: provider) }, set: { settings.setModel($0, for: provider) }),
                presets: provider.modelPresets,
                available: keyModel.availableModels
            )
        }

        if provider != .openAI {
            Section {
                let binding = Binding(get: { settings.baseURL(for: provider) }, set: { settings.setBaseURL($0, for: provider) })
                TextField("API base URL", text: binding)
                    .font(.system(.body, design: .monospaced))
                if OpenAIConfiguration.validatedBaseURL(binding.wrappedValue) == nil {
                    Text("Invalid URL — the default \(provider.defaultBaseURL.absoluteString) will be used.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
                Button("Reset to Default") { settings.setBaseURL(provider.defaultBaseURL.absoluteString, for: provider) }
            } header: {
                Text("Address")
            } footer: {
                Text(provider.requiresAPIKey ? "Your API key is sent to this address." : "Requests and your meeting transcript are sent to this address.")
                    .foregroundStyle(.secondary)
            }
            .task {
                // Local servers answer instantly: load their model list right away.
                guard !provider.requiresAPIKey, keyModel.availableModels.isEmpty else { return }
                await keyModel.testConnection()
                if settings.model(for: provider).isEmpty, let first = keyModel.availableModels.first {
                    settings.setModel(first, for: provider)
                }
            }
        }
    }
}
