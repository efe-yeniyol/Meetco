import AppKit
import SwiftUI

/// Renders a single suggestion of any kind.
struct SuggestionContentView: View {
    let suggestion: Suggestion
    var compact: Bool

    @Environment(SuggestionEngine.self) private var suggestions
    @Environment(SettingsStore.self) private var settings
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: compact ? 8 : 12) {
            header
            switch suggestion.kind {
            case .answer, .whatToSay: answerBody
            case .recap: recapBody
            case .questionsToAsk: questionsBody
            }
            footer
        }
        .textSelection(.enabled)
    }

    // MARK: Sections

    private var header: some View {
        HStack(spacing: 6) {
            Image(systemName: suggestion.kind.systemImage)
                .font(.system(size: 10.5))
            Text(suggestion.kind.title.uppercased())
                .font(Theme.sectionFont)
                .tracking(0.6)
            if let language = suggestion.languageName {
                TagLabel(text: language)
            }
            if suggestion.usedScreenContext {
                TagLabel(text: "Screen")
            }
            if !suggestion.directedAtUser {
                TagLabel(text: "Not addressed to you")
            }
            Spacer()
            Text(suggestion.createdAt, style: .time)
                .font(.system(size: 10.5))
                .monospacedDigit()
        }
        .foregroundStyle(.tertiary)
    }

    @ViewBuilder
    private var answerBody: some View {
        if let question = suggestion.question, !question.isEmpty {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Image(systemName: "questionmark.bubble")
                    .font(.system(size: 11))
                    .foregroundStyle(.tertiary)
                Text(question)
                    .font(.system(size: compact ? 12 : 13))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }

        Text(suggestion.text)
            .font(compact ? .system(size: 14) : Theme.answerFont)
            .lineSpacing(compact ? 2 : 4)
            .fixedSize(horizontal: false, vertical: true)
            .padding(compact ? 12 : 16)
            .frame(maxWidth: .infinity, alignment: .leading)
            .cardSurface(cornerRadius: Theme.cardCornerRadius, fill: Theme.subtleFill)

        if suggestion.needsMoreInfo {
            Label("Some details may need confirmation — avoid committing to specifics.", systemImage: "info.circle")
                .font(.system(size: 11.5))
                .foregroundStyle(.secondary)
        }

        if let explanation = suggestion.explanation, !explanation.isEmpty {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                TagLabel(text: settings.explanationLanguage.badge.isEmpty ? "NOTE" : settings.explanationLanguage.badge)
                Text(explanation)
                    .font(.system(size: compact ? 12 : 13))
                    .foregroundStyle(.secondary)
                    .lineSpacing(2)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    @ViewBuilder
    private var recapBody: some View {
        Text(suggestion.text)
            .font(.system(size: compact ? 13 : 14))
            .lineSpacing(3)
            .fixedSize(horizontal: false, vertical: true)
        bulletList("Key points", suggestion.items.map(\.text))
        bulletList("Action items", suggestion.actionItems)
        bulletList("Open questions", suggestion.openQuestions)
    }

    @ViewBuilder
    private var questionsBody: some View {
        VStack(alignment: .leading, spacing: 8) {
            ForEach(suggestion.items) { item in
                QuestionItemRow(item: item, badge: settings.explanationLanguage.badge)
            }
        }
    }

    @ViewBuilder
    private func bulletList(_ title: String, _ items: [String]) -> some View {
        if !items.isEmpty {
            VStack(alignment: .leading, spacing: 5) {
                SectionHeader(title: title)
                ForEach(items, id: \.self) { item in
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Circle().fill(Color.secondary.opacity(0.6)).frame(width: 4, height: 4)
                        Text(item)
                            .font(.system(size: 12.5))
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            .padding(.top, 2)
        }
    }

    private var footer: some View {
        HStack(spacing: 8) {
            QuietButton(title: copied ? "Copied" : "Copy", systemImage: copied ? "checkmark" : "doc.on.doc") {
                Clipboard.copy(suggestion.clipboardText)
                copied = true
                Task {
                    try? await Task.sleep(for: .seconds(1.4))
                    copied = false
                }
            }
            QuietButton(title: "Regenerate", systemImage: "arrow.clockwise") {
                suggestions.regenerate(suggestion)
            }
            .disabled(suggestions.isGenerating(suggestion.kind))
            QuietButton(title: "Dismiss", systemImage: "xmark") {
                suggestions.dismiss(suggestion)
            }
            Spacer()
            if let confidence = suggestion.confidence, suggestion.kind == .answer || suggestion.kind == .whatToSay {
                Text("\(Int((confidence * 100).rounded()))% confident")
                    .font(.system(size: 10.5))
                    .foregroundStyle(.tertiary)
                    .monospacedDigit()
                    .help("Model-reported confidence")
            }
        }
        .padding(.top, 2)
    }
}

struct QuestionItemRow: View {
    let item: SuggestionItem
    let badge: String
    @State private var copied = false

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            VStack(alignment: .leading, spacing: 4) {
                Text(item.text)
                    .font(.system(size: 13.5))
                    .fixedSize(horizontal: false, vertical: true)
                if let detail = item.detail, !detail.isEmpty {
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        if !badge.isEmpty { TagLabel(text: badge) }
                        Text(detail)
                            .font(.system(size: 12))
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            Spacer(minLength: 0)
            IconButton(systemName: copied ? "checkmark" : "doc.on.doc", help: "Copy question", size: 24) {
                Clipboard.copy(item.text)
                copied = true
                Task {
                    try? await Task.sleep(for: .seconds(1.4))
                    copied = false
                }
            }
        }
        .padding(12)
        .cardSurface()
    }
}

/// Shows the attached screenshot that will be sent with the next request.
struct ScreenContextChip: View {
    let context: ScreenContext
    let onRemove: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            if let image = NSImage(data: context.jpegData) {
                Image(nsImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
                    .frame(width: 56, height: 34)
                    .clipShape(RoundedRectangle(cornerRadius: 5, style: .continuous))
                    .overlay(RoundedRectangle(cornerRadius: 5, style: .continuous).strokeBorder(Theme.hairline, lineWidth: 0.5))
            }
            VStack(alignment: .leading, spacing: 2) {
                Text("Screen attached")
                    .font(.system(size: 12, weight: .medium))
                Text("Sent with your next suggestion request, then discarded.")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
            Spacer()
            IconButton(systemName: "xmark", help: "Remove screenshot", size: 22, action: onRemove)
        }
        .padding(8)
        .cardSurface(cornerRadius: 10)
    }
}
