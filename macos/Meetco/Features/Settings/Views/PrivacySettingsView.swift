import AppKit
import SwiftUI

struct PrivacySettingsView: View {
    let archive: SessionArchive

    @Environment(SettingsStore.self) private var settings
    @Environment(TranscriptStore.self) private var transcript
    @Environment(SuggestionEngine.self) private var suggestions
    @State private var savedCount = 0
    @State private var confirmClear = false

    var body: some View {
        @Bindable var settings = settings
        Form {
            Section("How your data is handled") {
                VStack(alignment: .leading, spacing: 8) {
                    bullet("Audio is captured only while you are listening and is never written to disk.")
                    bullet("Speech audio and transcript excerpts are sent to OpenAI with your own API key for transcription and suggestions. OpenAI's API data policies apply.")
                    bullet("Screenshots are only taken when you press the camera button, and are sent once with your next request.")
                    bullet("No Meetco account, servers, analytics or telemetry.")
                    bullet("Your API key is stored in the macOS Keychain.")
                }
                .font(.callout)
                .foregroundStyle(.secondary)
            }

            Section {
                Toggle("Save transcripts on this Mac", isOn: $settings.saveTranscriptsLocally)
                LabeledContent("Saved sessions", value: "\(savedCount)")
                HStack {
                    Button("Show in Finder") {
                        try? FileManager.default.createDirectory(at: archive.directory, withIntermediateDirectories: true)
                        NSWorkspace.shared.open(archive.directory)
                    }
                    Button("Delete Saved Transcripts…", role: .destructive) { confirmClear = true }
                        .disabled(savedCount == 0)
                }
            } header: {
                Text("Local history")
            } footer: {
                Text("When enabled, the transcript and suggestions of each session are saved as JSON in ~/Library/Application Support/Meetco/Sessions.")
                    .foregroundStyle(.secondary)
            }

            Section("Current session") {
                Button("Clear Current Transcript and Suggestions") {
                    transcript.clear()
                    suggestions.reset()
                }
            }
        }
        .formStyle(.grouped)
        .onAppear { savedCount = archive.sessionCount() }
        .confirmationDialog("Delete all saved transcripts?", isPresented: $confirmClear) {
            Button("Delete", role: .destructive) {
                try? archive.clearAll()
                savedCount = archive.sessionCount()
            }
        } message: {
            Text("This cannot be undone.")
        }
    }

    private func bullet(_ text: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Text("•")
            Text(text).fixedSize(horizontal: false, vertical: true)
        }
    }
}
