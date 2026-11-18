import SwiftUI

@main
struct MeetcoApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        // Meetco's windows are AppKit floating panels managed by WindowCoordinator.
        // This empty Settings scene only exists to satisfy SwiftUI's App requirements.
        Settings {
            EmptyView()
        }
        .commands {
            CommandGroup(replacing: .appSettings) {
                Button("Settings…") { appDelegate.showSettings() }
                    .keyboardShortcut(",", modifiers: .command)
            }
            CommandGroup(replacing: .newItem) {}
            CommandMenu("Meeting") {
                Button("Start / Stop Listening") { appDelegate.perform(.toggleListening) }
                Button("What Should I Say?") { appDelegate.perform(.whatShouldISay) }
                Button("Recap So Far") { appDelegate.perform(.recap) }
                Divider()
                Button("Show / Hide Panel") { appDelegate.perform(.togglePanel) }
                Button("Toggle Compact Mode") { appDelegate.perform(.toggleCompact) }
                Button("Show / Hide Meetco") { appDelegate.perform(.toggleEverything) }
            }
        }
    }
}
