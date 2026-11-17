import AppKit
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var environment: AppEnvironment?
    private var windows: WindowCoordinator?
    private let hotKeys = HotKeyManager()

    private var isRunningTests: Bool {
        ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard !isRunningTests else { return }
        let environment = AppEnvironment()
        self.environment = environment
        let windows = WindowCoordinator(environment: environment)
        self.windows = windows
        windows.start()

        hotKeys.onAction = { [weak self] action in self?.perform(action) }
        observeContinuously { [weak self] in
            guard let self, let environment = self.environment else { return }
            if environment.settings.globalShortcutsEnabled {
                self.hotKeys.registerAll()
            } else {
                self.hotKeys.unregisterAll()
            }
        }
        Log.app.info("Meetco launched")
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        environment?.appState.isControlBarVisible = true
        environment?.appState.isPanelVisible = true
        return true
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    func applicationWillTerminate(_ notification: Notification) {
        environment?.session.shutdown()
        hotKeys.unregisterAll()
    }

    func showSettings() {
        windows?.showSettings()
    }

    func perform(_ action: ShortcutAction) {
        guard let environment else { return }
        switch action {
        case .toggleListening: environment.session.toggleListening()
        case .whatShouldISay:
            environment.appState.show(.answers)
            environment.suggestions.whatShouldISay()
        case .recap:
            environment.appState.show(.answers)
            environment.suggestions.recap()
        case .togglePanel: environment.appState.togglePanel()
        case .toggleCompact: environment.appState.isCompact.toggle()
        case .toggleEverything: environment.appState.toggleEverything()
        }
    }
}
