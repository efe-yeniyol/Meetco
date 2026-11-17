import SwiftUI

/// App-level navigation actions exposed to SwiftUI views without coupling them to AppKit windows.
struct AppActions: Sendable {
    var openSettings: @MainActor @Sendable () -> Void = {}
    var openOnboarding: @MainActor @Sendable () -> Void = {}
    var closeOnboarding: @MainActor @Sendable () -> Void = {}
}

extension EnvironmentValues {
    @Entry var appActions = AppActions()
}
