import Foundation
import SwiftUI

/// Dependency-injection container: builds every service once and wires them together.
@MainActor
final class AppEnvironment {
    let settings: SettingsStore
    let appState: AppState
    let keyProvider: APIKeyProvider
    let keys: APIKeyRegistry
    let openAI: OpenAIClient
    /// Model listing per assistant provider, used by Settings to test keys and fill model pickers.
    let modelListers: [AssistantProvider: any ModelListing]
    let permissions: PermissionsManager
    let transcriptStore: TranscriptStore
    let transcription: TranscriptionCoordinator
    let suggestions: SuggestionEngine
    let session: MeetingSessionController
    let archive: SessionArchive

    init(
        keyStore: APIKeyStoring = KeychainManager(),
        providerKeyStore: (AssistantProvider) -> APIKeyStoring = { KeychainManager(provider: $0) }
    ) {
        let settings = SettingsStore()
        let keyProvider = APIKeyProvider(store: keyStore)
        let keys = APIKeyRegistry(openAI: keyProvider, store: providerKeyStore)
        let openAI = OpenAIClient(keyProvider: keyProvider)
        var chatClients: [AssistantProvider: any OpenAIClientProtocol] = [.openAI: openAI]
        var modelListers: [AssistantProvider: any ModelListing] = [.openAI: openAI]
        for provider in AssistantProvider.allCases where provider.wire == .openAICompatible && provider != .openAI {
            let client = OpenAIClient(
                keyProvider: keys.keyProvider(for: provider),
                configuration: { OpenAIConfiguration.current(for: provider) },
                requiresAPIKey: provider.requiresAPIKey
            )
            chatClients[provider] = client
            modelListers[provider] = client
        }
        let anthropic = AnthropicClient(keyProvider: keys.keyProvider(for: .anthropic))
        modelListers[.anthropic] = anthropic
        let transcriptStore = TranscriptStore()
        let liveTranscription = LiveTranscriptionHub(keyProvider: keyProvider)
        let onDeviceTranscription = OnDeviceTranscriptionService()
        let transcription = TranscriptionCoordinator(
            service: TranscriptionRouter(
                openAI: LiveFirstTranscriptionService(hub: liveTranscription, file: OpenAITranscriptionService(client: openAI)),
                onDevice: onDeviceTranscription
            ),
            store: transcriptStore,
            settings: settings
        )
        let suggestions = SuggestionEngine(
            assistant: ProviderRoutingAssistant(chatClients: chatClients, anthropic: anthropic),
            screenCapture: ScreenshotService(),
            transcript: transcriptStore,
            settings: settings,
            keys: keys
        )
        let permissions = PermissionsManager()
        let archive = SessionArchive()

        self.settings = settings
        self.appState = AppState(settings: settings)
        self.keyProvider = keyProvider
        self.keys = keys
        self.openAI = openAI
        self.modelListers = modelListers
        self.permissions = permissions
        self.transcriptStore = transcriptStore
        self.transcription = transcription
        self.suggestions = suggestions
        self.archive = archive
        self.session = MeetingSessionController(
            settings: settings,
            permissions: permissions,
            keyProvider: keyProvider,
            transcription: transcription,
            transcript: transcriptStore,
            suggestions: suggestions,
            archive: archive,
            liveTranscription: liveTranscription,
            onDeviceTranscription: onDeviceTranscription,
            warmUp: { [openAI, modelListers] in
                // Open connections ahead of time so the first transcription and answer skip the TLS handshake.
                async let transcription: Void = openAI.warmUp()
                let raw = UserDefaults.standard.string(forKey: SettingsKeys.assistantProvider) ?? ""
                if let provider = AssistantProvider(rawValue: raw), provider != .openAI {
                    _ = try? await modelListers[provider]?.listModels(apiKeyOverride: nil)
                }
                await transcription
            }
        )
    }
}

extension View {
    /// Injects every observable model a Meetco view may read.
    @MainActor
    func meetcoEnvironment(_ environment: AppEnvironment, actions: AppActions) -> some View {
        self
            .environment(environment.settings)
            .environment(environment.appState)
            .environment(environment.permissions)
            .environment(environment.transcriptStore)
            .environment(environment.transcription)
            .environment(environment.suggestions)
            .environment(environment.session)
            .environment(\.appActions, actions)
    }
}
