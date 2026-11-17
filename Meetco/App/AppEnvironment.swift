import Foundation
import SwiftUI

/// Dependency-injection container: builds every service once and wires them together.
@MainActor
final class AppEnvironment {
    let settings: SettingsStore
    let appState: AppState
    let keyProvider: APIKeyProvider
    let openAI: OpenAIClient
    let permissions: PermissionsManager
    let transcriptStore: TranscriptStore
    let transcription: TranscriptionCoordinator
    let suggestions: SuggestionEngine
    let session: MeetingSessionController
    let archive: SessionArchive

    init(keyStore: APIKeyStoring = KeychainManager()) {
        let settings = SettingsStore()
        let keyProvider = APIKeyProvider(store: keyStore)
        let openAI = OpenAIClient(keyProvider: keyProvider)
        let transcriptStore = TranscriptStore()
        let liveTranscription = LiveTranscriptionHub(keyProvider: keyProvider)
        let transcription = TranscriptionCoordinator(
            service: LiveFirstTranscriptionService(hub: liveTranscription, file: OpenAITranscriptionService(client: openAI)),
            store: transcriptStore,
            settings: settings
        )
        let suggestions = SuggestionEngine(
            assistant: OpenAIMeetingAssistant(client: openAI),
            screenCapture: ScreenshotService(),
            transcript: transcriptStore,
            settings: settings,
            keyProvider: keyProvider
        )
        let permissions = PermissionsManager()
        let archive = SessionArchive()

        self.settings = settings
        self.appState = AppState(settings: settings)
        self.keyProvider = keyProvider
        self.openAI = openAI
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
            warmUp: { [openAI] in await openAI.warmUp() }
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
