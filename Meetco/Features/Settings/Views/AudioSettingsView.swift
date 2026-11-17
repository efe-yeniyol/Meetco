import SwiftUI

struct AudioSettingsView: View {
    @Environment(SettingsStore.self) private var settings
    @Environment(MeetingSessionController.self) private var session
    @State private var devices: [AudioInputDevice] = []

    var body: some View {
        @Bindable var settings = settings
        Form {
            Section {
                Picker("Audio source", selection: Binding(get: { settings.captureMode }, set: { session.setCaptureMode($0) })) {
                    ForEach(AudioCaptureMode.allCases) { Text($0.title).tag($0) }
                }
                Picker("Microphone", selection: $settings.microphoneDeviceUID) {
                    Text("System default").tag(String?.none)
                    ForEach(devices) { Text($0.name).tag(Optional($0.id)) }
                }
                .disabled(!settings.captureMode.includesMicrophone)
            } header: {
                Text("Capture")
            } footer: {
                Text("Use headphones in Mic + System mode so the microphone doesn't pick up participants a second time. Microphone changes apply the next time you start listening.")
                    .foregroundStyle(.secondary)
            }

            Section("Transcription") {
                Picker("Spoken language", selection: $settings.transcriptionLanguage) {
                    ForEach(TranscriptionLanguage.allCases) { Text($0.title).tag($0) }
                }
                Picker("Latency", selection: $settings.latencyProfile) {
                    ForEach(LatencyProfile.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                Text(settings.latencyProfile.detail)
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }

            Section("Permissions") {
                PermissionsList()
            }
        }
        .formStyle(.grouped)
        .onAppear { devices = AudioDeviceManager.inputDevices() }
    }
}
