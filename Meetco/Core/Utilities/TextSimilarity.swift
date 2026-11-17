import Foundation

/// Lightweight text comparison used to avoid duplicate questions and segments.
enum TextSimilarity {
    static func normalized(_ text: String) -> String {
        // Turkish dotless/dotted i are separate letters, not diacritics, so map them explicitly.
        text.replacingOccurrences(of: "ı", with: "i")
            .replacingOccurrences(of: "İ", with: "i")
            .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)
            .components(separatedBy: CharacterSet.alphanumerics.inverted)
            .filter { !$0.isEmpty }
            .joined(separator: " ")
    }

    static func words(_ text: String) -> Set<String> {
        Set(normalized(text).split(separator: " ").map(String.init).filter { $0.count > 1 })
    }

    /// Jaccard similarity of the word sets (0…1).
    static func jaccard(_ lhs: String, _ rhs: String) -> Double {
        let a = words(lhs), b = words(rhs)
        if a.isEmpty && b.isEmpty { return normalized(lhs) == normalized(rhs) ? 1 : 0 }
        let union = a.union(b).count
        guard union > 0 else { return 0 }
        return Double(a.intersection(b).count) / Double(union)
    }
}
