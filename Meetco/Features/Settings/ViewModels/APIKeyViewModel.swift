import Foundation
import Observation
import SwiftUI

/// Non-observable services the settings screens need.
struct SettingsDependencies: Sendable {
    let keyProvider: APIKeyProvider
    let client: any OpenAIClientProtocol
    let archive: SessionArchive
}

extension EnvironmentValues {
    @Entry var settingsDependencies: SettingsDependencies? = nil
}

/// Manages entering, saving, testing and deleting the user's OpenAI API key.
@MainActor
@Observable
final class APIKeyViewModel {
    enum Status: Equatable {
        case notConfigured
        case saved
        case testing
        case connected(modelCount: Int)
        case failed(String)

        var message: String {
            switch self {
            case .notConfigured: "No API key saved."
            case .saved: "API key saved in your Keychain."
            case .testing: "Testing connection…"
            case .connected(let count): "Connected to OpenAI — \(count) models available."
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

    @ObservationIgnored private let keyProvider: APIKeyProvider
    @ObservationIgnored private let client: any OpenAIClientProtocol

    init(keyProvider: APIKeyProvider, client: any OpenAIClientProtocol) {
        self.keyProvider = keyProvider
        self.client = client
        if let key = keyProvider.currentKey(), !key.isEmpty {
            keyInput = key
            hasStoredKey = true
            status = .saved
        }
    }

    var trimmedInput: String { keyInput.trimmingCharacters(in: .whitespacesAndNewlines) }

    var maskedStoredKey: String? {
        guard hasStoredKey, let key = keyProvider.currentKey() else { return nil }
        return APIKeyProvider.maskedPreview(of: key)
    }

    var inputLooksValid: Bool { trimmedInput.count >= 20 && !trimmedInput.contains(" ") }

    func save() {
        guard inputLooksValid else {
            status = .failed("That doesn't look like an OpenAI API key (they usually start with \"sk-\").")
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
        guard !key.isEmpty else {
            status = .failed("Enter an API key first.")
            return
        }
        status = .testing
        do {
            let models = try await client.listModels(apiKeyOverride: key)
            availableModels = models
            status = .connected(modelCount: models.count)
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
