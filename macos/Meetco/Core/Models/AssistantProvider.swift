import Foundation

/// The service that writes answers, recaps and questions.
///
/// Transcription always uses OpenAI; the assistant can use any of these. Everything except
/// Anthropic speaks the OpenAI Chat Completions protocol, so it shares `OpenAIClient`.
enum AssistantProvider: String, CaseIterable, Identifiable, Codable, Sendable {
    case openAI, anthropic, deepSeek, ollama, lmStudio, custom

    var id: String { rawValue }

    enum Wire: Sendable { case openAICompatible, anthropic }

    var wire: Wire { self == .anthropic ? .anthropic : .openAICompatible }

    var title: String {
        switch self {
        case .openAI: "OpenAI"
        case .anthropic: "Anthropic (Claude)"
        case .deepSeek: "DeepSeek"
        case .ollama: "Ollama (local)"
        case .lmStudio: "LM Studio (local)"
        case .custom: "Custom (OpenAI-compatible)"
        }
    }

    /// Short name for messages ("Add your DeepSeek API key…").
    var shortName: String {
        switch self {
        case .openAI: "OpenAI"
        case .anthropic: "Anthropic"
        case .deepSeek: "DeepSeek"
        case .ollama: "Ollama"
        case .lmStudio: "LM Studio"
        case .custom: "custom provider"
        }
    }

    var defaultBaseURL: URL {
        switch self {
        case .openAI: OpenAIConfiguration.defaultBaseURL
        case .anthropic: URL(string: "https://api.anthropic.com/v1")!
        case .deepSeek: URL(string: "https://api.deepseek.com/v1")!
        case .ollama: URL(string: "http://localhost:11434/v1")!
        case .lmStudio: URL(string: "http://localhost:1234/v1")!
        case .custom: URL(string: "http://localhost:8080/v1")!
        }
    }

    /// Local servers and many self-hosted gateways need no key.
    var requiresAPIKey: Bool {
        switch self {
        case .openAI, .anthropic, .deepSeek: true
        case .ollama, .lmStudio, .custom: false
        }
    }

    var isLocal: Bool { self == .ollama || self == .lmStudio }

    var keyPlaceholder: String {
        switch self {
        case .openAI, .deepSeek: "sk-…"
        case .anthropic: "sk-ant-…"
        case .ollama, .lmStudio, .custom: "Optional"
        }
    }

    var keyPageURL: URL? {
        switch self {
        case .openAI: URL(string: "https://platform.openai.com/api-keys")
        case .anthropic: URL(string: "https://console.anthropic.com/settings/keys")
        case .deepSeek: URL(string: "https://platform.deepseek.com/api_keys")
        case .ollama: URL(string: "https://ollama.com/download")
        case .lmStudio: URL(string: "https://lmstudio.ai")
        case .custom: nil
        }
    }

    /// Model used until the user picks one. Empty for local servers: the model list comes from the server.
    var defaultModel: String {
        switch self {
        case .openAI: ModelCatalog.defaultAssistantModel
        case .anthropic: "claude-haiku-5-5"
        case .deepSeek: "deepseek-chat"
        case .ollama, .lmStudio, .custom: ""
        }
    }

    var modelPresets: [ModelCatalog.Entry] {
        switch self {
        case .openAI: ModelCatalog.assistantModels
        case .anthropic: [
            .init(id: "claude-haiku-5-5", note: "Fastest, great for live answers (recommended)"),
            .init(id: "claude-sonnet-5-5", note: "Smarter, slightly slower"),
            .init(id: "claude-opus-5-5", note: "Most capable, slowest to start"),
        ]
        case .deepSeek: [
            .init(id: "deepseek-chat", note: "Fast and inexpensive (recommended)"),
            .init(id: "deepseek-reasoner", note: "Thinks first: much slower to start"),
        ]
        case .ollama, .lmStudio, .custom: []
        }
    }

    var capabilities: ChatCapabilities {
        switch self {
        case .openAI: ChatCapabilities(images: true, structuredOutput: .jsonSchema, tokenLimit: .maxCompletionTokens, reasoningEffort: true)
        case .anthropic: ChatCapabilities(images: true, structuredOutput: .none, tokenLimit: .maxTokens, reasoningEffort: false)
        // DeepSeek has no image input and only supports `json_object`.
        case .deepSeek: ChatCapabilities(images: false, structuredOutput: .jsonObject, tokenLimit: .maxTokens, reasoningEffort: false)
        // Depends on the loaded model; unsupported features are dropped on the first 400.
        case .ollama, .lmStudio, .custom: ChatCapabilities(images: true, structuredOutput: .jsonSchema, tokenLimit: .maxTokens, reasoningEffort: false)
        }
    }

    /// Keychain location of the provider's key. OpenAI keeps the original item so existing keys still work.
    var keychainService: String {
        self == .openAI ? "io.github.meetco.Meetco.openai" : "io.github.meetco.Meetco.\(rawValue)"
    }

    var keychainAccount: String {
        self == .openAI ? "openai-api-key" : "api-key"
    }
}

/// What a chat endpoint accepts, so requests are right the first time instead of after a 400.
struct ChatCapabilities: Sendable, Equatable {
    enum StructuredOutput: Sendable, Equatable { case jsonSchema, jsonObject, none }
    enum TokenLimit: Sendable, Equatable { case maxCompletionTokens, maxTokens }

    var images: Bool
    var structuredOutput: StructuredOutput
    var tokenLimit: TokenLimit
    var reasoningEffort: Bool
}
