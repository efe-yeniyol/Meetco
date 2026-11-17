import SwiftUI

/// First-launch setup: API key, permissions, privacy notice.
struct OnboardingView: View {
    @Environment(\.settingsDependencies) private var dependencies
    @Environment(SettingsStore.self) private var settings
    @Environment(\.appActions) private var actions

    var body: some View {
        ZStack {
            VisualEffectBackground(material: .underWindowBackground)
                .ignoresSafeArea()
            if let dependencies {
                OnboardingContent(dependencies: dependencies) {
                    settings.hasCompletedOnboarding = true
                    actions.closeOnboarding()
                }
            }
        }
        .frame(width: 560, height: 680)
    }
}

private struct OnboardingContent: View {
    @State private var keyModel: APIKeyViewModel
    let onFinish: () -> Void

    init(dependencies: SettingsDependencies, onFinish: @escaping () -> Void) {
        _keyModel = State(initialValue: APIKeyViewModel(keyProvider: dependencies.keyProvider, client: dependencies.client))
        self.onFinish = onFinish
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 26) {
                VStack(alignment: .leading, spacing: 8) {
                    Image(systemName: "waveform")
                        .font(.system(size: 28, weight: .semibold))
                        .padding(.bottom, 4)
                    Text("Welcome to Meetco")
                        .font(.system(size: 26, weight: .semibold))
                    Text("Your AI companion for every conversation.")
                        .font(.title3)
                        .foregroundStyle(.secondary)
                    Text("Meetco listens alongside your Google Meet and Microsoft Teams calls, transcribes the conversation and suggests what to say. It never joins, speaks or sends messages for you.")
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.top, 4)
                }

                step(number: 1, title: "Add your OpenAI API key") {
                    BYOKExplanation()
                    APIKeyEditor(model: keyModel)
                }

                step(number: 2, title: "Allow audio access") {
                    PermissionsList()
                }

                step(number: 3, title: "Privacy") {
                    Text("Audio is processed in memory and sent to OpenAI for transcription using your key — Meetco does not process everything locally. Nothing is recorded to disk, and transcripts are only saved if you turn that on in Settings → Privacy.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                HStack {
                    Text(keyModel.hasStoredKey ? "You're ready. Press ⌃⌥L to start listening." : "Save an API key to continue.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button("Start Using Meetco", action: onFinish)
                        .controlSize(.large)
                        .keyboardShortcut(.defaultAction)
                        .disabled(!keyModel.hasStoredKey)
                }
            }
            .padding(.horizontal, 36)
            .padding(.top, 44)
            .padding(.bottom, 28)
        }
    }

    private func step<Content: View>(number: Int, title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 8) {
                Text("\(number)")
                    .font(.system(size: 11, weight: .bold, design: .rounded))
                    .frame(width: 20, height: 20)
                    .background(Circle().fill(Theme.selectedFill))
                Text(title).font(.headline)
            }
            content()
        }
        .padding(18)
        .cardSurface(cornerRadius: 14)
    }
}
