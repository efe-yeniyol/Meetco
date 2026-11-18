import Foundation

/// One key/value pair of an ordered JSON object.
struct JSONField: Sendable, Equatable {
    let key: String
    let value: JSONValue
}

/// A minimal JSON value used to describe JSON schemas for structured outputs.
/// Objects keep their key order (models generate structured output in schema order,
/// which lets Meetco stream the most important fields first).
indirect enum JSONValue: Sendable, Equatable {
    case string(String)
    case integer(Int)
    case number(Double)
    case bool(Bool)
    case array([JSONValue])
    case object([JSONField])
    case null

    subscript(key: String) -> JSONValue? {
        guard case .object(let fields) = self else { return nil }
        return fields.first { $0.key == key }?.value
    }

    /// Serializes to compact JSON text, preserving object key order.
    func serialized() -> String {
        switch self {
        case .string(let value): return Self.quote(value)
        case .integer(let value): return String(value)
        case .number(let value):
            return value.isFinite ? String(value) : "null"
        case .bool(let value): return value ? "true" : "false"
        case .null: return "null"
        case .array(let values): return "[" + values.map { $0.serialized() }.joined(separator: ",") + "]"
        case .object(let fields):
            return "{" + fields.map { Self.quote($0.key) + ":" + $0.value.serialized() }.joined(separator: ",") + "}"
        }
    }

    private static func quote(_ string: String) -> String {
        var result = "\""
        for scalar in string.unicodeScalars {
            switch scalar {
            case "\"": result += "\\\""
            case "\\": result += "\\\\"
            case "\n": result += "\\n"
            case "\r": result += "\\r"
            case "\t": result += "\\t"
            case _ where scalar.value < 0x20: result += String(format: "\\u%04x", scalar.value)
            default: result.unicodeScalars.append(scalar)
            }
        }
        return result + "\""
    }
}

extension JSONValue: ExpressibleByStringLiteral, ExpressibleByIntegerLiteral, ExpressibleByFloatLiteral,
    ExpressibleByBooleanLiteral, ExpressibleByArrayLiteral, ExpressibleByDictionaryLiteral {
    init(stringLiteral value: String) { self = .string(value) }
    init(integerLiteral value: Int) { self = .integer(value) }
    init(floatLiteral value: Double) { self = .number(value) }
    init(booleanLiteral value: Bool) { self = .bool(value) }
    init(arrayLiteral elements: JSONValue...) { self = .array(elements) }
    init(dictionaryLiteral elements: (String, JSONValue)...) {
        self = .object(elements.map { JSONField(key: $0.0, value: $0.1) })
    }
}

/// Incrementally extracts values from a JSON object that is still being streamed.
enum PartialJSON {
    /// Returns the (possibly incomplete) string value for `key`, and whether it is complete.
    static func string(forKey key: String, in text: String) -> (value: String, isComplete: Bool)? {
        guard let keyRange = text.range(of: "\"\(key)\"") else { return nil }
        var index = keyRange.upperBound
        func skipWhitespace() {
            while index < text.endIndex, text[index].isWhitespace { index = text.index(after: index) }
        }
        skipWhitespace()
        guard index < text.endIndex, text[index] == ":" else { return nil }
        index = text.index(after: index)
        skipWhitespace()
        guard index < text.endIndex, text[index] == "\"" else { return nil }
        index = text.index(after: index)

        var result = ""
        while index < text.endIndex {
            let character = text[index]
            if character == "\\" {
                let next = text.index(after: index)
                guard next < text.endIndex else { return (result, false) }
                switch text[next] {
                case "n": result.append("\n")
                case "t": result.append("\t")
                case "r": break
                case "u":
                    let hexStart = text.index(after: next)
                    guard let hexEnd = text.index(hexStart, offsetBy: 4, limitedBy: text.endIndex),
                          let code = UInt32(text[hexStart..<hexEnd], radix: 16) else { return (result, false) }
                    if let scalar = Unicode.Scalar(code) { result.unicodeScalars.append(scalar) }
                    index = hexEnd
                    continue
                default: result.append(text[next])
                }
                index = text.index(after: next)
                continue
            }
            if character == "\"" { return (result, true) }
            result.append(character)
            index = text.index(after: index)
        }
        return (result, false)
    }

    static func bool(forKey key: String, in text: String) -> Bool? {
        guard let keyRange = text.range(of: "\"\(key)\"") else { return nil }
        let rest = text[keyRange.upperBound...].drop { $0.isWhitespace || $0 == ":" }
        if rest.hasPrefix("true") { return true }
        if rest.hasPrefix("false") { return false }
        return nil
    }
}
