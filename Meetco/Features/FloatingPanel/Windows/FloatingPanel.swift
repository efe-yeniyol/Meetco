import AppKit

/// Non-activating panel that floats above meeting apps without stealing focus.
final class FloatingPanel: NSPanel {
    var allowsKeyStatus = true

    override var canBecomeKey: Bool { allowsKeyStatus }
    override var canBecomeMain: Bool { false }

    /// Borderless glass toolbar.
    static func makeControlBar() -> FloatingPanel {
        let panel = FloatingPanel(
            contentRect: NSRect(x: 0, y: 0, width: 480, height: 48),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.allowsKeyStatus = false
        panel.configureCommon()
        return panel
    }

    /// Resizable glass panel with a hidden, transparent title bar.
    static func makeMainPanel(size: NSSize) -> FloatingPanel {
        let panel = FloatingPanel(
            contentRect: NSRect(origin: .zero, size: size),
            styleMask: [.titled, .resizable, .fullSizeContentView, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.titlebarAppearsTransparent = true
        panel.titleVisibility = .hidden
        panel.title = "Meetco"
        for button in [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton] {
            panel.standardWindowButton(button)?.isHidden = true
        }
        panel.becomesKeyOnlyIfNeeded = true
        panel.minSize = NSSize(width: 340, height: 170)
        panel.configureCommon()
        return panel
    }

    private func configureCommon() {
        isFloatingPanel = true
        level = .floating
        hidesOnDeactivate = false
        isMovableByWindowBackground = true
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        isReleasedWhenClosed = false
        animationBehavior = .utilityWindow
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
    }
}
