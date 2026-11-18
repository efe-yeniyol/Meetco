import Foundation
import Observation
import SwiftUI

/// Non-observable services the settings screens need.
struct SettingsDependencies: Sendable {
    let keyProvider: APIKeyProvider
    let client: any OpenAIClientProtocol
    let archive: SessionArchive
    let keys: APIKeyRegistry
    let modelListers: [AssistantProvider: any ModelListing]

    @MainActor
    func keyModel(for provider: AssistantProvider) -> APIKeyViewModel {
        APIKeyViewModel(provider: provider, keyProvider: keys.keyProvider(for: provider), client: modelListers[provider] ?? client)
    }
}

extension EnvironmentValues {
    @Entry var settingsDependencies: SettingsDependencies? = nil
}

/// Manages entering, saving, testing and deleting one provider's API key.
@MainActor
@Observable
final class APIKeyViewModel {
    enum Status: Equatable {
        case notConfigured
        case saved
        case testing
        case connected(service: String, modelCount: Int)
        case failed(String)

        var message: String {
            switch self {
            case .notConfigured: "No API key saved."
            case .saved: "API key saved in your Keychain."
            case .testing: "Testing connection…"
            case .connected(let service, let count): "Connected to \(service) — \(count) models available."
            case .failed(let message): message
            }
        }

        var systemImage: String {
            switch self {
            case .notConfigured: "key"
            case .saved: "lock"
            case .testing: "hourglass"
            case .connected: "checkmark.circle"
            case .failed: "exclamationmark.triangle"
            }
        }
    }

    var keyInput = ""
    var isRevealed = false
    private(set) var status: Status = .notConfigured
    private(set) var hasStoredKey = false
    private(set) var availableModels: [String] = []

    let provider: AssistantProvider
    @ObservationIgnored private let keyProvider: APIKeyProvider
    @ObservationIgnored private let client: any ModelListing

    init(provider: AssistantProvider = .openAI, keyProvider: APIKeyProvider, client: any ModelListing) {
        self.provider = provider
        self.keyProvider = keyProvider
        self.client = client
        if let key = keyProvider.currentKey(), !key.isEmpty {
            keyInput = key
            hasStoredKey = true
            status = .saved
        } else if !provider.requiresAPIKey {
            status = .notConfigured
        }
    }

    /// Ready to use: a key is saved, or the provider needs none.
    var isReady: Bool { hasStoredKey || !provider.requiresAPIKey }

    var trimmedInput: String { keyInput.trimmingCharacters(in: .whitespacesAndNewlines) }

    var maskedStoredKey: String? {
        guard hasStoredKey, let key = keyProvider.currentKey() else { return nil }
        return APIKeyProvider.maskedPreview(of: key)
    }

    var inputLooksValid: Bool {
        guard !trimmedInput.contains(" ") else { return false }
        return provider.requiresAPIKey ? trimmedInput.count >= 20 : !trimmedInput.isEmpty
    }

    func save() {
        guard inputLooksValid else {
            status = .failed("That doesn't look like a\(provider == .anthropic ? "n" : "") \(provider.shortName) API key\(provider.requiresAPIKey ? " (they usually start with \"\(provider.keyPlaceholder.dropLast())\")" : "").")
            return
        }
        do {
            try keyProvider.save(trimmedInput)
            keyInput = trimmedInput
            hasStoredKey = true
            status = .saved
        } catch {
            status = .failed(error.localizedDescription)
        }
    }

    /// Verifies the key currently in the field (saved or not) by listing models.
    func testConnection() async {
        let key = trimmedInput
        guard !key.isEmpty || !provider.requiresAPIKey else {
            status = .failed("Enter an API key first.")
            return
        }
        status = .testing
        do {
            let models = try await client.listModels(apiKeyOverride: key.isEmpty ? nil : key)
            availableModels = models
            status = .connected(service: provider.shortName, modelCount: models.count)
        } catch let error as OpenAIError where provider != .openAI {
            status = .failed(ProviderError(provider: provider, underlying: error).localizedDescription)
        } catch {
            status = .failed(error.localizedDescription)
        }
    }

    func saveAndTest() async {
        save()
        guard hasStoredKey, status == .saved else { return }
        await testConnection()
    }

    func delete() {
        do {
            try keyProvider.delete()
            keyInput = ""
            hasStoredKey = false
            availableModels = []
            status = .notConfigured
        } catch {
            status = .failed(error.localizedDescription)
        }
    }
}
