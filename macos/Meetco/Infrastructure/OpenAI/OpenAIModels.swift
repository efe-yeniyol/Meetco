import Foundation

// MARK: - Chat Completions

struct ChatMessage: Encodable, Sendable {
    enum Content: Sendable {
        case text(String)
        case parts([ContentPart])
    }

    struct ContentPart: Encodable, Sendable {
        struct ImageURL: Encodable, Sendable {
            let url: String
            let detail: String
        }

        let type: String
        var text: String?
        var imageURL: ImageURL?

        enum CodingKeys: String, CodingKey {
            case type, text
            case imageURL = "image_url"
        }

        static func text(_ value: String) -> ContentPart { ContentPart(type: "text", text: value) }

        static func jpeg(_ data: Data, detail: String = "auto") -> ContentPart {
            ContentPart(
                type: "image_url",
                imageURL: ImageURL(url: "data:image/jpeg;base64,\(data.base64EncodedString())", detail: detail)
            )
        }
    }

    let role: String
    let content: Content

    static func system(_ text: String) -> ChatMessage { ChatMessage(role: "system", content: .text(text)) }
    static func user(_ text: String) -> ChatMessage { ChatMessage(role: "user", content: .text(text)) }
    static func user(parts: [ContentPart]) -> ChatMessage { ChatMessage(role: "user", content: .parts(parts)) }

    enum CodingKeys: String, CodingKey { case role, content }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(role, forKey: .role)
        switch content {
        case .text(let text): try container.encode(text, forKey: .content)
        case .parts(let parts): try container.encode(parts, forKey: .content)
        }
    }

    var hasImage: Bool {
        if case .parts(let parts) = content { return parts.contains { $0.imageURL != nil } }
        return false
    }
}

struct ResponseFormat: Encodable, Sendable {
    struct JSONSchemaSpec: Encodable, Sendable {
        static let placeholder = "__MEETCO_ORDERED_SCHEMA__"
        let name: String
        let strict: Bool
        let schema: JSONValue

        enum CodingKeys: String, CodingKey { case name, strict, schema }

        func encode(to encoder: Encoder) throws {
            var container = encoder.container(keyedBy: CodingKeys.self)
            try container.encode(name, forKey: .name)
            try container.encode(strict, forKey: .strict)
            // JSONEncoder does not keep key order; the ordered schema is spliced in by `encodedBody()`.
            try container.encode(Self.placeholder, forKey: .schema)
        }
    }

    let type: String
    var jsonSchema: JSONSchemaSpec?

    enum CodingKeys: String, CodingKey {
        case type
        case jsonSchema = "json_schema"
    }

    static func jsonSchema(name: String, schema: JSONValue) -> ResponseFormat {
        ResponseFormat(type: "json_schema", jsonSchema: JSONSchemaSpec(name: name, strict: true, schema: schema))
    }

    static let jsonObject = ResponseFormat(type: "json_object")
}

struct ChatCompletionRequest: Encodable, Sendable {
    var model: String
    var messages: [ChatMessage]
    var responseFormat: ResponseFormat?
    var temperature: Double?
    var maxCompletionTokens: Int?
    var reasoningEffort: String?
    var stream: Bool?
    /// Older name of `max_completion_tokens`, still the one most OpenAI-compatible servers know.
    var maxTokens: Int?

    enum CodingKeys: String, CodingKey {
        case model, messages, temperature, stream
        case responseFormat = "response_format"
        case maxCompletionTokens = "max_completion_tokens"
        case maxTokens = "max_tokens"
        case reasoningEffort = "reasoning_effort"
    }
}

extension ChatCompletionRequest {
    /// JSON body with the response schema serialized in its declared key order.
    func encodedBody() throws -> Data {
        let data = try JSONEncoder().encode(self)
        guard let schema = responseFormat?.jsonSchema?.schema,
              var text = String(data: data, encoding: .utf8) else { return data }
        text = text.replacingOccurrences(of: "\"\(ResponseFormat.JSONSchemaSpec.placeholder)\"", with: schema.serialized())
        return Data(text.utf8)
    }
}

/// One server-sent event of a streamed chat completion.
struct ChatCompletionChunk: Decodable, Sendable {
    struct Choice: Decodable, Sendable {
        struct Delta: Decodable, Sendable {
            let content: String?
            let refusal: String?
        }
        let delta: Delta
        let finishReason: String?

        enum CodingKeys: String, CodingKey {
            case delta
            case finishReason = "finish_reason"
        }
    }
    let choices: [Choice]
}

struct ChatCompletionResponse: Decodable, Sendable {
    struct Choice: Decodable, Sendable {
        struct Message: Decodable, Sendable {
            let content: String?
            let refusal: String?
        }
        let message: Message
        let finishReason: String?

        enum CodingKeys: String, CodingKey {
            case message
            case finishReason = "finish_reason"
        }
    }

    let choices: [Choice]
}

// MARK: - Transcription

struct TranscriptionResponse: Decodable, Sendable {
    let text: String
    /// Only returned by `whisper-1` with `verbose_json` (English language name, e.g. "turkish").
    let language: String?
    /// Detected languages reported by `gpt-transcribe` (ISO codes), most likely first.
    let languages: [DetectedLanguage]?

    struct DetectedLanguage: Decodable, Sendable { let code: String }

    init(text: String, language: String?, languages: [DetectedLanguage]? = nil) {
        self.text = text
        self.language = language
        self.languages = languages
    }
}

// MARK: - Models

struct ModelListResponse: Decodable, Sendable {
    struct Model: Decodable, Sendable { let id: String }
    let data: [Model]
}

struct OpenAIErrorEnvelope: Decodable {
    struct Body: Decodable {
        let message: String?
        let code: String?
        let type: String?
    }
    let error: Body
}
