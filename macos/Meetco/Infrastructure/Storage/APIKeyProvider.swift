import Foundation
import Observation

/// Thread-safe, in-memory cache in front of the Keychain so the key is read once per launch.
final class APIKeyProvider: @unchecked Sendable {
    private let store: APIKeyStoring
    private let lock = NSLock()
    private var cached: String?
    private var loaded = false

    init(store: APIKeyStoring) {
        self.store = store
    }

    /// Returns the configured key, or nil. Never logs the value.
    func currentKey() -> String? {
        lock.withLock {
            if !loaded {
                do {
                    cached = try store.loadAPIKey()
                } catch {
                    Log.storage.error("Unable to read API key from Keychain: \(error.localizedDescription, privacy: .public)")
                    cached = nil
                }
                loaded = true
            }
            return cached
        }
    }

    var hasKey: Bool {
        guard let key = currentKey() else { return false }
        return !key.isEmpty
    }

    func save(_ key: String) throws {
        let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
        try store.saveAPIKey(trimmed)
        lock.withLock {
            cached = trimmed
            loaded = true
        }
    }

    func delete() throws {
        try store.deleteAPIKey()
        lock.withLock {
            cached = nil
            loaded = true
        }
    }

    /// "sk-…abcd" style preview for the UI.
    static func maskedPreview(of key: String) -> String {
        guard key.count > 8 else { return String(repeating: "•", count: key.count) }
        return "\(key.prefix(3))…\(key.suffix(4))"
    }
}
