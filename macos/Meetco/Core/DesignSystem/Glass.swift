import AppKit
import SwiftUI

/// Wraps a SwiftUI hierarchy in a native glass surface:
/// Liquid Glass (`NSGlassEffectView`) on macOS 26, vibrancy (`NSVisualEffectView`) before that.
@MainActor
enum GlassContainer {
    static func make<Content: View>(rootView: Content, cornerRadius: CGFloat) -> NSView {
        let hosting = NSHostingView(rootView: rootView)
        hosting.translatesAutoresizingMaskIntoConstraints = false
        // A colour layer under the content guarantees the chosen tint shows on every surface,
        // including large panels where the glass tint alone is very faint.
        let tintView = TintView(cornerRadius: cornerRadius)
        tintView.translatesAutoresizingMaskIntoConstraints = false

        if #available(macOS 26.0, *) {
            let glass = NSGlassEffectView()
            glass.cornerRadius = cornerRadius
            glass.tintColor = tintColor(for: .neutral)
            let wrapper = NSView()
            wrapper.addSubview(tintView)
            pin(tintView, to: wrapper)
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
            effect.addSubview(tintView)
            pin(tintView, to: effect)
            effect.addSubview(hosting)
            pin(hosting, to: effect)
            return effect
        }
    }

    /// Recolours a surface made by `make` without rebuilding its content.
    static func applyTint(_ tint: GlassTint, to view: NSView) {
        if #available(macOS 26.0, *), let glass = view as? NSGlassEffectView {
            glass.tintColor = tintColor(for: tint)
        }
        tintView(in: view)?.color = tint == .neutral ? .clear : overlayColor(for: tint)
    }

    private static func tintView(in view: NSView) -> TintView? {
        if let match = view as? TintView { return match }
        for child in view.subviews { if let match = tintView(in: child) { return match } }
        if #available(macOS 26.0, *), let glass = view as? NSGlassEffectView, let content = glass.contentView {
            return tintView(in: content)
        }
        return nil
    }

    /// Colour wash drawn under the content of a tinted surface.
    static func overlayColor(for tint: GlassTint) -> NSColor {
        NSColor(name: nil) { appearance in
            let dark = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            return swatchColor(for: tint).withAlphaComponent(dark ? 0.30 : 0.22)
        }
    }

    /// The tint for `tint`, strong enough to keep text legible over busy meeting video.
    static func tintColor(for tint: GlassTint) -> NSColor {
        NSColor(name: nil) { appearance in
            let dark = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            switch tint {
            case .neutral:
                return dark ? NSColor.black.withAlphaComponent(0.28) : NSColor.white.withAlphaComponent(0.30)
            default:
                return swatchColor(for: tint).withAlphaComponent(dark ? 0.42 : 0.30)
            }
        }
    }

    /// Fully saturated colour for each tint, used for swatches and as the tint base.
    static func swatchColor(for tint: GlassTint) -> NSColor {
        switch tint {
        case .neutral: NSColor.gray
        case .teal: NSColor(srgbRed: 0.00, green: 0.60, blue: 0.64, alpha: 1)
        case .orange: NSColor(srgbRed: 0.98, green: 0.52, blue: 0.12, alpha: 1)
        case .blue: NSColor(srgbRed: 0.16, green: 0.42, blue: 0.95, alpha: 1)
        case .purple: NSColor(srgbRed: 0.55, green: 0.30, blue: 0.90, alpha: 1)
        case .green: NSColor(srgbRed: 0.20, green: 0.68, blue: 0.32, alpha: 1)
        }
    }

    /// Rounded colour layer used to tint glass surfaces.
    private final class TintView: NSView {
        var color: NSColor = .clear { didSet { needsDisplay = true } }

        init(cornerRadius: CGFloat) {
            super.init(frame: .zero)
            wantsLayer = true
            layer?.cornerRadius = cornerRadius
            layer?.cornerCurve = .continuous
            layer?.masksToBounds = true
        }

        @available(*, unavailable)
        required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

        override var wantsUpdateLayer: Bool { true }

        override func updateLayer() {
            effectiveAppearance.performAsCurrentDrawingAppearance {
                layer?.backgroundColor = color.cgColor
            }
        }

        override func viewDidChangeEffectiveAppearance() {
            super.viewDidChangeEffectiveAppearance()
            needsDisplay = true
        }

        override func hitTest(_ point: NSPoint) -> NSView? { nil }
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

/// Colour wash for regular windows (Settings, onboarding) matching the panels' glass tint.
struct TintedWindowBackground: View {
    @Environment(SettingsStore.self) private var settings

    var body: some View {
        if settings.glassTint != .neutral {
            Color(nsColor: GlassContainer.overlayColor(for: settings.glassTint))
                .opacity(0.6)
                .ignoresSafeArea()
                .allowsHitTesting(false)
        }
    }
}

/// Uses the chosen glass colour as the accent for controls (toggles, selections, buttons).
private struct GlassAccentModifier: ViewModifier {
    @Environment(SettingsStore.self) private var settings

    func body(content: Content) -> some View {
        if settings.glassTint == .neutral {
            content
        } else {
            content.tint(Color(nsColor: GlassContainer.swatchColor(for: settings.glassTint)))
        }
    }
}

extension View {
    /// Applies the glass colour as the controls' accent. Needs `SettingsStore` in the environment.
    func glassAccent() -> some View { modifier(GlassAccentModifier()) }

    /// Subtle card surface: thin fill and hairline border, no heavy shadows.
    func cardSurface(cornerRadius: CGFloat = Theme.cardCornerRadius, fill: Color = Theme.subtleFill) -> some View {
        background(fill, in: RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .strokeBorder(Theme.hairline, lineWidth: 0.5)
            )
    }
}
