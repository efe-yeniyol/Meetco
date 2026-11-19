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

            Section {
                Picker("Transcribe with", selection: $settings.transcriptionProvider) {
                    ForEach(TranscriptionProvider.allCases) { provider in
                        Text(provider.title).tag(provider)
                            .disabled(provider == .onDevice && !TranscriptionProvider.isOnDeviceAvailable)
                    }
                }
                Text(engineDetail)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
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
            } header: {
                Text("Transcription")
            }

            Section("Permissions") {
                PermissionsList()
            }
        }
        .formStyle(.grouped)
        .onAppear { devices = AudioDeviceManager.inputDevices() }
    }

    private var engineDetail: String {
        guard TranscriptionProvider.isOnDeviceAvailable else {
            return "On-device transcription needs macOS 26 or later. Audio is sent to OpenAI with your key."
        }
        switch settings.transcriptionProvider {
        case .openAI:
            return "Audio is sent to OpenAI with your key. Best accuracy and automatic language detection; the model is chosen in the OpenAI tab."
        case .onDevice:
            return "Apple's speech model runs on this Mac: audio never leaves it and there is no cost. The model for each language downloads once. Choose the spoken language for best results; \"Detect automatically\" uses your Mac's language."
        }
    }
}
