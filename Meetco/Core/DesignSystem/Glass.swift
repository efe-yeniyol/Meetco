import AppKit
import SwiftUI

/// Wraps a SwiftUI hierarchy in a native glass surface:
/// Liquid Glass (`NSGlassEffectView`) on macOS 26, vibrancy (`NSVisualEffectView`) before that.
@MainActor
enum GlassContainer {
    static func make<Content: View>(rootView: Content, cornerRadius: CGFloat) -> NSView {
        let hosting = NSHostingView(rootView: rootView)
        hosting.translatesAutoresizingMaskIntoConstraints = false

        if #available(macOS 26.0, *) {
            let glass = NSGlassEffectView()
            glass.cornerRadius = cornerRadius
            glass.tintColor = legibilityTint
            let wrapper = NSView()
            wrapper.addSubview(hosting)
            pin(hosting, to: wrapper)
            glass.contentView = wrapper
            return glass
        } else {
            let effect = NSVisualEffectView()
            effect.material = .hudWindow
            effect.blendingMode = .behindWindow
            effect.state = .active
            effect.maskImage = roundedMask(radius: cornerRadius)
            effect.addSubview(hosting)
            pin(hosting, to: effect)
            return effect
        }
    }

    /// A light tint that keeps text legible over busy meeting video.
    private static var legibilityTint: NSColor {
        NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
                ? NSColor.black.withAlphaComponent(0.28)
                : NSColor.white.withAlphaComponent(0.30)
        }
    }

    private static func pin(_ view: NSView, to container: NSView) {
        NSLayoutConstraint.activate([
            view.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            view.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            view.topAnchor.constraint(equalTo: container.topAnchor),
            view.bottomAnchor.constraint(equalTo: container.bottomAnchor),
        ])
    }

    static func roundedMask(radius: CGFloat) -> NSImage {
        let edge = radius * 2 + 1
        let image = NSImage(size: NSSize(width: edge, height: edge), flipped: false) { rect in
            NSColor.black.setFill()
            NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius).fill()
            return true
        }
        image.capInsets = NSEdgeInsets(top: radius, left: radius, bottom: radius, right: radius)
        image.resizingMode = .stretch
        return image
    }
}

/// SwiftUI wrapper for system vibrancy materials (used for non-floating windows).
struct VisualEffectBackground: NSViewRepresentable {
    var material: NSVisualEffectView.Material = .underWindowBackground
    var blending: NSVisualEffectView.BlendingMode = .behindWindow

    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = material
        view.blendingMode = blending
        view.state = .active
        return view
    }

    func updateNSView(_ view: NSVisualEffectView, context: Context) {
        view.material = material
        view.blendingMode = blending
    }
}

extension View {
    /// Subtle card surface: thin fill and hairline border, no heavy shadows.
    func cardSurface(cornerRadius: CGFloat = Theme.cardCornerRadius, fill: Color = Theme.subtleFill) -> some View {
        background(fill, in: RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .strokeBorder(Theme.hairline, lineWidth: 0.5)
            )
    }
}
