import SwiftUI

/// Root of the floating glass panel. Focused by default: a slim header and the live answer.
/// The tools sidebar is optional (toggle in the header).
struct MainPanelView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        VStack(spacing: 0) {
            PanelHeader()
            Rectangle().fill(Theme.hairline).frame(height: 1)
            HStack(spacing: 0) {
                if appState.showsSidebar && !appState.isCompact {
                    SidebarView()
                        .frame(width: 200)
                        .transition(.move(edge: .leading).combined(with: .opacity))
                    Rectangle().fill(Theme.hairline).frame(width: 1)
                }
                Group {
                    switch appState.selectedTab {
                    case .answers: LiveAnswerView()
                    case .transcript: TranscriptView()
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .animation(.easeInOut(duration: 0.18), value: appState.showsSidebar)
    }
}

/// Slim header: listening status, tabs, tools and size toggles.
struct PanelHeader: View {
    @Environment(AppState.self) private var appState
    @Environment(MeetingSessionController.self) private var session

    var body: some View {
        HStack(spacing: 8) {
            Button {
                session.toggleListening()
            } label: {
                HStack(spacing: 6) {
                    RecordingDot(isActive: session.isListening)
                    Group {
                        if session.isListening, let start = session.startedAt {
                            ElapsedTimeText(start: start)
                        } else {
                            Text(session.isBusy ? "…" : "Not listening")
                        }
                    }
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(.secondary)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(session.isListening ? "Stop listening (⌃⌥L)" : "Start listening (⌃⌥L)")

            Spacer(minLength: 8)
            TabSwitcher(compact: true)
            Spacer(minLength: 8)

            if !appState.isCompact {
                IconButton(systemName: "sidebar.left", help: "Tools: people, recap, questions to ask, history", isActive: appState.showsSidebar, size: 24) {
                    appState.showsSidebar.toggle()
                }
            }
            IconButton(
                systemName: appState.isCompact ? "arrow.up.left.and.arrow.down.right" : "arrow.down.right.and.arrow.up.left",
                help: appState.isCompact ? "Larger panel (⌃⌥C)" : "Smaller panel (⌃⌥C)",
                size: 24
            ) {
                appState.isCompact.toggle()
            }
        }
        .padding(.horizontal, 12)
        .frame(height: 38)
    }
}

/// Segmented control styled for glass surfaces.
struct TabSwitcher: View {
    @Environment(AppState.self) private var appState
    @Namespace private var selection
    var compact = false

    var body: some View {
        HStack(spacing: 2) {
            ForEach(PanelTab.allCases) { tab in
                let isSelected = appState.selectedTab == tab
                Button {
                    withAnimation(.snappy(duration: 0.2)) { appState.selectedTab = tab }
                } label: {
                    Text(tab.title)
                        .font(.system(size: compact ? 11 : 12, weight: .medium))
                        .foregroundStyle(isSelected ? Color.primary : Color.secondary)
                        .padding(.horizontal, compact ? 9 : 11)
                        .padding(.vertical, compact ? 3 : 4)
                        .background {
                            if isSelected {
                                Capsule(style: .continuous)
                                    .fill(Theme.selectedFill)
                                    .matchedGeometryEffect(id: "tab", in: selection)
                            }
                        }
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(2)
        .background(Capsule(style: .continuous).fill(Theme.subtleFill))
        .overlay(Capsule(style: .continuous).strokeBorder(Theme.hairline, lineWidth: 0.5))
    }
}
