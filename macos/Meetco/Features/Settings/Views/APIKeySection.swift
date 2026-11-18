import SwiftUI

/// Reusable API key editor used by Settings and onboarding.
struct APIKeyEditor: View {
    @Bindable var model: APIKeyViewModel
    @State private var confirmDelete = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 6) {
                Group {
                    if model.isRevealed {
                        TextField("sk-…", text: $model.keyInput)
                    } else {
                        SecureField("sk-…", text: $model.keyInput)
                    }
                }
                .textFieldStyle(.roundedBorder)
                .font(.system(.body, design: .monospaced))
                .onSubmit { Task { await model.saveAndTest() } }

                Button {
                    model.isRevealed.toggle()
                } label: {
                    Image(systemName: model.isRevealed ? "eye.slash" : "eye")
                }
                .buttonStyle(.borderless)
                .help(model.isRevealed ? "Hide API key" : "Show API key")
            }

            HStack(spacing: 8) {
                Button("Save API Key") { model.save() }
                    .disabled(model.trimmedInput.isEmpty)
                Button("Test Connection") { Task { await model.testConnection() } }
                    .disabled(model.trimmedInput.isEmpty || model.status == .testing)
                Spacer()
                if model.hasStoredKey {
                    Button("Delete Key", role: .destructive) { confirmDelete = true }
                }
            }

            HStack(spacing: 6) {
                if model.status == .testing {
                    ProgressView().controlSize(.small)
                } else {
                    Image(systemName: model.status.systemImage)
                }
                Text(model.status.message)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .font(.callout)
            .foregroundStyle(.secondary)
        }
        .confirmationDialog("Delete the saved OpenAI API key?", isPresented: $confirmDelete) {
            Button("Delete Key", role: .destructive) { model.delete() }
        } message: {
            Text("Meetco will stop transcribing and suggesting until you add a key again.")
        }
    }
}

/// Explains the BYOK model and links to the OpenAI dashboard.
struct BYOKExplanation: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Meetco uses your own OpenAI account. Requests are billed to your OpenAI API usage — there is no Meetco account, subscription or server.")
            Text("You need an OpenAI API key with billing (credits) set up. A ChatGPT subscription alone does not include API access.")
            HStack(spacing: 14) {
                Link("Create an API key", destination: URL(string: "https://platform.openai.com/api-keys")!)
                Link("Billing settings", destination: URL(string: "https://platform.openai.com/settings/organization/billing/overview")!)
            }
            .padding(.top, 2)
        }
        .font(.callout)
        .foregroundStyle(.secondary)
        .fixedSize(horizontal: false, vertical: true)
    }
}
