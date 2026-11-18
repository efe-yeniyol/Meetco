import SwiftUI

/// Live, chronological transcript with automatic scrolling.
struct TranscriptView: View {
    @Environment(TranscriptStore.self) private var transcript
    @Environment(TranscriptionCoordinator.self) private var transcription
    @Environment(SettingsStore.self) private var settings
    @Environment(MeetingSessionController.self) private var session
    @State private var copied = false

    private let bottomID = "transcript-bottom"

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            if let error = transcription.lastError {
                InlineBanner(message: error) { transcription.clearError() }
                    .padding(.horizontal, 14)
                    .padding(.bottom, 6)
            }
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 8) {
                        if transcript.segments.isEmpty {
                            emptyState
                        }
                        ForEach(transcript.segments) { segment in
                            TranscriptRow(segment: segment)
                                .id(segment.id)
                        }
                        Color.clear.frame(height: 1).id(bottomID)
                    }
                    .padding(.horizontal, 14)
                    .padding(.bottom, 14)
                }
                .onChange(of: transcript.segments.count) {
                    guard settings.autoScrollTranscript else { return }
                    withAnimation(.easeOut(duration: 0.2)) { proxy.scrollTo(bottomID, anchor: .bottom) }
                }
                .onAppear { proxy.scrollTo(bottomID, anchor: .bottom) }
            }
        }
    }

    private var toolbar: some View {
        HStack(spacing: 8) {
            Text("\(transcript.segments.count) segments")
                .font(.system(size: 11))
                .foregroundStyle(.tertiary)
            Spacer()
            Toggle(isOn: Binding(get: { settings.autoScrollTranscript }, set: { settings.autoScrollTranscript = $0 })) {
                Text("Auto-scroll").font(.system(size: 11))
            }
            .toggleStyle(.checkbox)
            .controlSize(.small)
            IconButton(systemName: copied ? "checkmark" : "doc.on.doc", help: "Copy transcript", size: 24) {
                Clipboard.copy(transcript.plainText)
                copied = true
                Task {
                    try? await Task.sleep(for: .seconds(1.4))
                    copied = false
                }
            }
            .disabled(transcript.segments.isEmpty)
            IconButton(systemName: "trash", help: "Clear transcript", size: 24) {
                transcript.clear()
            }
            .disabled(transcript.segments.isEmpty)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
    }

    private var emptyState: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(session.isListening ? "Listening…" : "No transcript yet")
                .font(.system(size: 14, weight: .semibold))
            Text(session.isListening
                 ? "Speech appears here a moment after each pause."
                 : "Start listening to transcribe the meeting in real time.")
                .font(.system(size: 12.5))
                .foregroundStyle(.secondary)
        }
        .padding(.top, 16)
    }
}

struct TranscriptRow: View {
    let segment: TranscriptSegment
    @Environment(SuggestionEngine.self) private var suggestions
    @Environment(AppState.self) private var appState
    @State private var isHovering = false

    private static let timeFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"
        return formatter
    }()

    private var isMicrophone: Bool { segment.source == .microphone }

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            RoundedRectangle(cornerRadius: 1)
                .fill(Color.primary.opacity(isMicrophone ? 0.55 : 0.18))
                .frame(width: 2)
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    Image(systemName: segment.source.systemImage)
                        .font(.system(size: 9))
                    Text(segment.source.speakerLabel)
                        .font(.system(size: 11, weight: .semibold))
                    Text(Self.timeFormatter.string(from: segment.startedAt))
                        .font(.system(size: 10.5))
                        .monospacedDigit()
                        .foregroundStyle(.tertiary)
                    if let code = segment.languageCode {
                        TagLabel(text: code.uppercased())
                            .help(segment.languageDisplayName ?? code)
                    }
                    if segment.isQuestion {
                        Image(systemName: "questionmark.circle")
                            .font(.system(size: 10))
                            .help("Detected question")
                    }
                    Spacer(minLength: 0)
                    if segment.isQuestion && isHovering {
                        Button {
                            appState.selectedTab = .answers
                            suggestions.answer(segment)
                        } label: {
                            Label("Answer", systemImage: "sparkles")
                                .font(.system(size: 10.5, weight: .medium))
                        }
                        .buttonStyle(.plain)
                        .foregroundStyle(.secondary)
                    }
                }
                .foregroundStyle(.secondary)

                Text(segment.text)
                    .font(.system(size: 13))
                    .foregroundStyle(isMicrophone ? .secondary : .primary)
                    .lineSpacing(2)
                    .fixedSize(horizontal: false, vertical: true)
                    .textSelection(.enabled)
            }
        }
        .padding(.vertical, 6)
        .padding(.horizontal, 8)
        .background(
            RoundedRectangle(cornerRadius: 9, style: .continuous)
                .fill(isMicrophone ? Theme.subtleFill : (isHovering ? Theme.subtleFill : .clear))
        )
        .onHover { isHovering = $0 }
    }
}
