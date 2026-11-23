import AppKit
import SwiftUI

/// Creates and manages Meetco's windows (control bar, main panel, settings, onboarding)
/// and keeps them in sync with `AppState` and `SettingsStore`.
@MainActor
final class WindowCoordinator {
    private enum SizeKeys {
        static let expanded = "window.expandedSize"
        static let compact = "window.compactSize"
    }

    static let defaultExpandedSize = NSSize(width: 600, height: 400)
    static let defaultCompactSize = NSSize(width: 440, height: 220)
    static let sidebarWidth: CGFloat = 201

    private let environment: AppEnvironment
    private var controlBar: FloatingPanel?
    private var mainPanel: FloatingPanel?
    private var settingsWindow: NSWindow?
    private var onboardingWindow: NSWindow?
    private var appliedCompact: Bool?
    private var appliedSidebar = false
    private var resizeObserver: NSObjectProtocol?

    init(environment: AppEnvironment) {
        self.environment = environment
    }

    private var actions: AppActions {
        AppActions(
            openSettings: { [weak self] in self?.showSettings() },
            openOnboarding: { [weak self] in self?.showOnboarding() },
            closeOnboarding: { [weak self] in self?.closeOnboarding() }
        )
    }

    func start() {
        buildControlBar()
        buildMainPanel()
        observeContinuously { [weak self] in self?.applyState() }

        let canTranscribe = environment.settings.transcriptionProvider == .onDevice || environment.keyProvider.hasKey
        if !canTranscribe || !environment.settings.hasCompletedOnboarding {
            showOnboarding()
        }
    }

    // MARK: Building

    private func buildControlBar() {
        let panel = FloatingPanel.makeControlBar()
        let root = ControlBarView().meetcoEnvironment(environment, actions: actions)
        let sizing = NSHostingView(rootView: root)
        let size = sizing.fittingSize
        panel.contentView = GlassContainer.make(rootView: root, cornerRadius: Theme.barCornerRadius)
        panel.setContentSize(size)

        if !panel.setFrameUsingName("MeetcoControlBar"), let screen = NSScreen.main?.visibleFrame {
            panel.setFrameOrigin(NSPoint(x: screen.midX - size.width / 2, y: screen.maxY - size.height - 12))
        }
        panel.setFrameAutosaveName("MeetcoControlBar")
        controlBar = panel
    }

    private func buildMainPanel() {
        let compact = environment.appState.isCompact
        let size = storedSize(compact: compact)
        let panel = FloatingPanel.makeMainPanel(size: size)
        let root = MainPanelView()
            .meetcoEnvironment(environment, actions: actions)
            .ignoresSafeArea()
        panel.contentView = GlassContainer.make(rootView: root, cornerRadius: Theme.panelCornerRadius)

        if !panel.setFrameUsingName("MeetcoMainPanel"), let screen = NSScreen.main?.visibleFrame {
            panel.setFrame(
                NSRect(x: screen.maxX - size.width - 24, y: screen.maxY - size.height - 76, width: size.width, height: size.height),
                display: false
            )
        }
        panel.setFrameAutosaveName("MeetcoMainPanel")
        appliedCompact = compact

        resizeObserver = NotificationCenter.default.addObserver(
            forName: NSWindow.didEndLiveResizeNotification,
            object: panel,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.rememberCurrentSize() }
        }
        mainPanel = panel
    }

    // MARK: State sync

    private func applyState() {
        let settings = environment.settings
        let state = environment.appState

        switch settings.appearance {
        case .dark: NSApp.appearance = NSAppearance(named: .darkAqua)
        case .light: NSApp.appearance = NSAppearance(named: .aqua)
        case .system: NSApp.appearance = nil
        }

        for panel in [controlBar, mainPanel].compactMap({ $0 }) {
            if let content = panel.contentView { GlassContainer.applyTint(settings.glassTint, to: content) }
            panel.level = settings.alwaysOnTop ? .floating : .normal
            panel.collectionBehavior = settings.showOnAllSpaces
                ? [.canJoinAllSpaces, .fullScreenAuxiliary]
                : [.moveToActiveSpace, .fullScreenAuxiliary]
            // Best effort: some capture tools on recent macOS versions ignore this flag.
            panel.sharingType = settings.hideFromScreenSharing ? .none : .readOnly
        }

        setVisible(controlBar, state.isControlBarVisible)
        setVisible(mainPanel, state.isPanelVisible)

        if appliedCompact != state.isCompact {
            applyCompact(state.isCompact)
        }
        let wantsSidebar = state.showsSidebar && !state.isCompact
        if wantsSidebar != appliedSidebar {
            applySidebar(wantsSidebar)
        }
    }

    private func setVisible(_ panel: NSPanel?, _ visible: Bool) {
        guard let panel else { return }
        if visible && !panel.isVisible {
            panel.orderFrontRegardless()
        } else if !visible && panel.isVisible {
            panel.orderOut(nil)
        }
    }

    private func applyCompact(_ compact: Bool) {
        guard let panel = mainPanel else { return }
        if let previous = appliedCompact { saveSize(sizeWithoutSidebar(panel.frame.size), compact: previous) }
        appliedCompact = compact
        appliedSidebar = false
        let size = storedSize(compact: compact)
        // Keep the top-left corner anchored while resizing.
        let frame = NSRect(x: panel.frame.minX, y: panel.frame.maxY - size.height, width: size.width, height: size.height)
        panel.setFrame(frame, display: true, animate: panel.isVisible)
    }

    /// Widens/narrows the panel so opening the sidebar doesn't squeeze the answer.
    private func applySidebar(_ visible: Bool) {
        appliedSidebar = visible
        guard let panel = mainPanel else { return }
        var frame = panel.frame
        frame.size.width += visible ? Self.sidebarWidth : -Self.sidebarWidth
        frame.size.width = max(panel.minSize.width, frame.size.width)
        panel.setFrame(frame, display: true, animate: panel.isVisible)
    }

    private func rememberCurrentSize() {
        guard let panel = mainPanel, let compact = appliedCompact else { return }
        saveSize(sizeWithoutSidebar(panel.frame.size), compact: compact)
    }

    private func sizeWithoutSidebar(_ size: NSSize) -> NSSize {
        appliedSidebar ? NSSize(width: size.width - Self.sidebarWidth, height: size.height) : size
    }

    private func storedSize(compact: Bool) -> NSSize {
        let key = compact ? SizeKeys.compact : SizeKeys.expanded
        if let string = UserDefaults.standard.string(forKey: key) {
            let size = NSSizeFromString(string)
            if size.width >= 300 && size.height >= 150 { return size }
        }
        return compact ? Self.defaultCompactSize : Self.defaultExpandedSize
    }

    private func saveSize(_ size: NSSize, compact: Bool) {
        UserDefaults.standard.set(NSStringFromSize(size), forKey: compact ? SizeKeys.compact : SizeKeys.expanded)
    }

    // MARK: Settings & onboarding

    func showSettings() {
        if settingsWindow == nil {
            let controller = NSHostingController(rootView: SettingsView().meetcoEnvironment(environment, actions: actions)
                .environment(\.settingsDependencies, SettingsDependencies(
                    keyProvider: environment.keyProvider,
                    client: environment.openAI,
                    archive: environment.archive,
                    keys: environment.keys,
                    modelListers: environment.modelListers
                )))
            let window = NSWindow(contentViewController: controller)
            window.title = "Meetco Settings"
            window.styleMask = [.titled, .closable, .miniaturizable]
            window.isReleasedWhenClosed = false
            window.setContentSize(NSSize(width: 600, height: 560))
            window.center()
            settingsWindow = window
        }
        NSApp.activate(ignoringOtherApps: true)
        settingsWindow?.makeKeyAndOrderFront(nil)
    }

    func showOnboarding() {
        // Floating panels sit above normal windows; keep them out of the way during setup.
        environment.appState.isPanelVisible = false
        environment.appState.isControlBarVisible = false
        if onboardingWindow == nil {
            let controller = NSHostingController(rootView: OnboardingView().meetcoEnvironment(environment, actions: actions)
                .environment(\.settingsDependencies, SettingsDependencies(
                    keyProvider: environment.keyProvider,
                    client: environment.openAI,
                    archive: environment.archive,
                    keys: environment.keys,
                    modelListers: environment.modelListers
                )))
            let window = NSWindow(contentViewController: controller)
            window.title = "Welcome to Meetco"
            window.styleMask = [.titled, .closable, .fullSizeContentView]
            window.titlebarAppearsTransparent = true
            window.titleVisibility = .hidden
            window.isMovableByWindowBackground = true
            window.isReleasedWhenClosed = false
            window.setContentSize(NSSize(width: 560, height: 680))
            window.center()
            NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: window, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    self?.environment.appState.isControlBarVisible = true
                    self?.environment.appState.isPanelVisible = true
                }
            }
            onboardingWindow = window
        }
        NSApp.activate(ignoringOtherApps: true)
        onboardingWindow?.makeKeyAndOrderFront(nil)
    }

    func closeOnboarding() {
        onboardingWindow?.close()
        environment.appState.isControlBarVisible = true
        environment.appState.isPanelVisible = true
    }
}
