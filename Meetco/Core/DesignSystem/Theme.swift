import SwiftUI

/// Design tokens for Meetco's restrained, monochrome glass interface.
enum Theme {
    static let panelCornerRadius: CGFloat = 18
    static let barCornerRadius: CGFloat = 24
    static let cardCornerRadius: CGFloat = 12
    static let controlCornerRadius: CGFloat = 8

    static let hairline = Color.primary.opacity(0.09)
    static let subtleFill = Color.primary.opacity(0.045)
    static let hoverFill = Color.primary.opacity(0.08)
    static let selectedFill = Color.primary.opacity(0.12)

    /// Muted recording red, used only for the "listening" indicator.
    static let recording = Color(nsColor: NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            ? NSColor(srgbRed: 1.0, green: 0.42, blue: 0.40, alpha: 1)
            : NSColor(srgbRed: 0.85, green: 0.22, blue: 0.20, alpha: 1)
    })

    static let answerFont = Font.system(size: 16, weight: .regular)
    static let sectionFont = Font.system(size: 10.5, weight: .semibold)
}
