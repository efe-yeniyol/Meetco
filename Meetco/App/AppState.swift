import Foundation
import Observation

enum PanelTab: String, CaseIterable, Identifiable {
    case answers, transcript

    var id: String { rawValue }
    var title: String { rawValue.capitalized }
    var systemImage: String {
        switch self {
        case .answers: "text.bubble"
        case .transcript: "text.alignleft"
        }
    }
}

/// Centralized UI state shared by the floating windows.
@MainActor
@Observable
final class AppState {
    var selectedTab: PanelTab = .answers
    var isPanelVisible = true
    var isControlBarVisible = true
    var isCompact: Bool
    /// The tools sidebar is hidden by default to keep the panel focused on question → answer.
    var showsSidebar = false

    init(settings: SettingsStore) {
        isCompact = settings.defaultPanelSize == .compact
    }

    func show(_ tab: PanelTab) {
        selectedTab = tab
        isControlBarVisible = true
        isPanelVisible = true
    }

    func togglePanel() {
        isPanelVisible.toggle()
        if isPanelVisible { isControlBarVisible = true }
    }

    func toggleEverything() {
        let visible = isControlBarVisible || isPanelVisible
        isControlBarVisible = !visible
        isPanelVisible = !visible
    }
}
