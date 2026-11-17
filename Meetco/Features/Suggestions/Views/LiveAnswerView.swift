import SwiftUI

/// The focused Answers view: the question, then the answer streaming in large type,
/// with the latest heard line at the bottom so it is always clear Meetco is listening.
struct LiveAnswerView: View {
    @Environment(SuggestionEngine.self) private var suggestions
    @Environment(MeetingSessionController.self) private var session
    @Environment(AppState.self) private var appState

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    banners
                    if let suggestion = suggestions.currentSuggestion {
                        if suggestion.kind == .answer || suggestion.kind == .whatToSay {
                            QuestionAnswerView(suggestion: suggestion, isStreaming: suggestions.isStreamingCurrent, compact: appState.isCompact)
                        } else {
                            SuggestionContentView(suggestion: suggestion, compact: appState.isCompact)
                        }
                    } else if suggestions.isGenerating {
                        ThinkingIndicator(label: "Answering…").padding(.top, 8)
                    } else {
                        emptyState
                    }
                }
                .padding(.horizontal, appState.isCompact ? 16 : 22)
                .padding(.top, 14)
                .padding(.bottom, 12)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .scrollIndicators(.never)
            LiveCaptionBar()
        }
    }

    @ViewBuilder
    private var banners: some View {
        if let message = session.errorMessage ?? session.warningMessage {
            InlineBanner(message: message, style: session.errorMessage != nil ? .error : .info) { session.dismissMessages() }
        }
        if let error = suggestions.lastError {
            InlineBanner(message: error) { suggestions.lastError = nil }
        }
        if let screen = suggestions.pendingScreenContext {
            ScreenContextChip(context: screen) { suggestions.pendingScreenContext = nil }
        }
    }

    private var emptyState: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(session.isListening ? "Listening. When you're asked a question, the answer appears here." : "Start listening, then join your call.")
                .font(.system(size: 14))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 8) {
                if !session.isListening {
                    QuietButton(title: "Start listening", systemImage: "waveform") { session.start() }
                }
                QuietButton(title: "What should I say?", systemImage: "quote.bubble") { suggestions.whatShouldISay() }
            }
        }
        .padding(.top, 6)
    }
}

/// Question → answer, the answer being the visual focus.
struct QuestionAnswerView: View {
    let suggestion: Suggestion
    let isStreaming: Bool
    let compact: Bool

    @Environment(SuggestionEngine.self) private var suggestions
    @Environment(SettingsStore.self) private var settings
    @State private var copied = false
    @State private var isHovering = false

    var body: some View {
        VStack(alignment: .leading, spacing: compact ? 8 : 12) {
            if let question = suggestion.question, !question.isEmpty {
                Text(question)
                    .font(.system(size: compact ? 12.5 : 13.5, weight: .medium))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .textSelection(.enabled)
            }

            if suggestion.text.isEmpty && isStreaming {
                ThinkingIndicator(label: "Answering…")
            } else {
                TypewriterText(text: suggestion.text, isStreaming: isStreaming)
                    .font(.system(size: compact ? 16 : 19, weight: .regular))
                    .lineSpacing(compact ? 3 : 5)
                    .fixedSize(horizontal: false, vertical: true)
                    .textSelection(.enabled)
                    .id(suggestion.id)
            }

            if suggestion.needsMoreInfo && !isStreaming {
                Label("Don't commit to specifics you're unsure of.", systemImage: "info.circle")
                    .font(.system(size: 11))
                    .foregroundStyle(.tertiary)
            }

            if let explanation = suggestion.explanation, !explanation.isEmpty {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    if !settings.explanationLanguage.badge.isEmpty {
                        TagLabel(text: settings.explanationLanguage.badge)
                    }
                    Text(explanation)
                        .font(.system(size: compact ? 12 : 13))
                        .foregroundStyle(.secondary)
                        .lineSpacing(2)
                        .fixedSize(horizontal: false, vertical: true)
                        .textSelection(.enabled)
                }
            }

            if !isStreaming {
                actions
            }
        }
        .onHover { isHovering = $0 }
    }

    private var actions: some View {
        HStack(spacing: 2) {
            IconButton(systemName: copied ? "checkmark" : "doc.on.doc", help: "Copy answer", size: 24) {
                Clipboard.copy(suggestion.clipboardText)
                copied = true
                Task {
                    try? await Task.sleep(for: .seconds(1.4))
                    copied = false
                }
            }
            IconButton(systemName: "arrow.clockwise", help: "Regenerate", size: 24) {
                suggestions.regenerate(suggestion)
            }
            .disabled(suggestions.isGenerating(suggestion.kind))
            IconButton(systemName: "xmark", help: "Dismiss", size: 24) {
                suggestions.dismiss(suggestion)
            }
            Spacer()
            if let position = suggestions.historyPosition, position.count > 1 {
                IconButton(systemName: "chevron.left", help: "Previous answer", size: 22) { suggestions.showOlder() }
                    .disabled(position.index >= position.count)
                Text("\(position.index)/\(position.count)")
                    .font(.system(size: 10.5))
                    .monospacedDigit()
                    .foregroundStyle(.tertiary)
                IconButton(systemName: "chevron.right", help: "Newer answer", size: 22) { suggestions.showNewer() }
                    .disabled(position.index <= 1)
            }
        }
        .opacity(isHovering ? 1 : 0.55)
        .animation(.easeOut(duration: 0.15), value: isHovering)
    }
}

/// Reveals text character by character, like a typewriter.
///
/// Model output arrives in uneven bursts (often a whole sentence at once). This view smooths
/// it into a steady flow without holding the text back: the further behind it is, the faster
/// it types, so it never lags more than a fraction of a second.
struct TypewriterText: View {
    let text: String
    let isStreaming: Bool

    /// Number of characters currently visible.
    @State private var shown: Int

    init(text: String, isStreaming: Bool) {
        self.text = text
        self.isStreaming = isStreaming
        // Finished answers (history, restored sessions) appear instantly; only live ones type.
        _shown = State(initialValue: isStreaming ? 0 : text.count)
    }

    var body: some View {
        let total = text.count
        let isTyping = shown < total
        (Text(isTyping ? String(text.prefix(shown)) : text)
            + Text(isStreaming || isTyping ? " ▍" : "").foregroundColor(.secondary))
            .task(id: text) { await type() }
    }

    private func type() async {
        let total = text.count
        if shown > total { shown = total }
        while shown < total {
            let backlog = total - shown
            shown += max(1, backlog / 6)
            do { try await Task.sleep(for: .milliseconds(16)) } catch { return }
        }
    }
}

/// One-line live caption of the latest speech; tap to open the full transcript.
struct LiveCaptionBar: View {
    @Environment(TranscriptStore.self) private var transcript
    @Environment(TranscriptionCoordinator.self) private var transcription
    @Environment(MeetingSessionController.self) private var session
    @Environment(AppState.self) private var appState

    var body: some View {
        Button {
            appState.selectedTab = .transcript
        } label: {
            HStack(spacing: 8) {
                if session.isListening {
                    LevelMeter(level: max(session.systemLevel, session.microphoneLevel))
                }
                caption
                    .font(.system(size: 11.5))
                    .lineLimit(1)
                    .truncationMode(.head)
                Spacer(minLength: 4)
                if transcription.isTranscribing {
                    ProgressView().controlSize(.mini)
                }
            }
            .padding(.horizontal, 14)
            .frame(height: 30)
            .frame(maxWidth: .infinity)
            .background(Theme.subtleFill)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("Open transcript")
    }

    private var caption: Text {
        if let last = transcript.segments.last {
            return Text(last.source.speakerLabel + "  ").fontWeight(.semibold).foregroundColor(.secondary)
                + Text(last.text).foregroundColor(.secondary)
        }
        return Text(session.isListening ? "Listening…" : "Not listening").foregroundColor(.secondary)
    }
}
