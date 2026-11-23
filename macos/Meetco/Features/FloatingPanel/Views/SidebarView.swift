import SwiftUI

/// Compact left sidebar: assist actions, detected/suggested questions, history.
struct SidebarView: View {
    @Environment(AppState.self) private var appState
    @Environment(SuggestionEngine.self) private var suggestions
    @Environment(TranscriptStore.self) private var transcript
    @Environment(SettingsStore.self) private var settings

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                brand

                VStack(alignment: .leading, spacing: 2) {
                    SectionHeader(title: "Assist")
                        .padding(.bottom, 4)
                    SidebarActionRow(title: "What should I say?", systemImage: "quote.bubble", isBusy: suggestions.isGenerating(.whatToSay)) {
                        appState.selectedTab = .answers
                        suggestions.whatShouldISay()
                    }
                    SidebarActionRow(title: "Questions to ask", systemImage: "questionmark.bubble", isBusy: suggestions.isGenerating(.questionsToAsk)) {
                        appState.selectedTab = .answers
                        suggestions.questionsToAsk()
                    }
                    SidebarActionRow(title: "Recap so far", systemImage: "list.bullet.rectangle", isBusy: suggestions.isGenerating(.recap)) {
                        appState.selectedTab = .answers
                        suggestions.recap()
                    }
                }

                if settings.meetingMemoryEnabled {
                    meetingMemory
                }

                section("Detected questions", empty: "Questions from participants appear here.", items: detectedQuestions) { segment in
                    SidebarTextRow(text: segment.text, systemImage: "questionmark.circle", help: "Generate an answer") {
                        appState.selectedTab = .answers
                        suggestions.answer(segment)
                    }
                }

                if let questions = suggestions.latestQuestionsToAsk, !questions.items.isEmpty {
                    section("Suggested questions", empty: "", items: questions.items) { item in
                        SidebarTextRow(text: item.text, systemImage: "doc.on.doc", help: "Copy question") {
                            Clipboard.copy(item.text)
                        }
                    }
                }

                section("Previous suggestions", empty: "AI suggestions will be listed here.", items: Array(suggestions.suggestions.prefix(20))) { suggestion in
                    SidebarTextRow(
                        text: suggestion.previewTitle,
                        systemImage: suggestion.kind.systemImage,
                        help: "Show suggestion",
                        isSelected: suggestion.id == suggestions.displayedSuggestion?.id
                    ) {
                        appState.selectedTab = .answers
                        suggestions.select(suggestion)
                    }
                }
            }
            .padding(.horizontal, 12)
            .padding(.top, 14)
            .padding(.bottom, 12)
        }
        .scrollIndicators(.never)
    }

    private var detectedQuestions: [TranscriptSegment] {
        let microphoneOnly = settings.captureMode == .microphoneOnly
        return transcript.segments
            .filter { $0.isQuestion && ($0.source == .system || microphoneOnly) }
            .suffix(8)
            .reversed()
    }

    /// People and key facts gathered from the conversation.
    @ViewBuilder
    private var meetingMemory: some View {
        let memory = suggestions.memory?.memory ?? MeetingMemory()
        VStack(alignment: .leading, spacing: 2) {
            SectionHeader(title: "People", trailing: memory.participants.isEmpty ? nil : "\(memory.participants.count)")
                .padding(.bottom, 4)
            if memory.participants.isEmpty {
                Text(suggestions.memory?.isUpdating == true ? "Listening for names and roles…" : "Names and roles appear here as people introduce themselves.")
                    .font(.system(size: 11))
                    .foregroundStyle(.tertiary)
                    .padding(.horizontal, 4)
            } else {
                ForEach(memory.participants) { PersonRow(person: $0) }
            }
        }
        if !memory.facts.isEmpty {
            section("Key points", empty: "", items: memory.facts.reversed().prefix(8).map { MemoryFact(text: $0) }) { fact in
                SidebarTextRow(text: fact.text, systemImage: "pin", help: "Copy") {
                    Clipboard.copy(fact.text)
                }
            }
        }
    }

    private var brand: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 6) {
                Image(systemName: "waveform")
                    .font(.system(size: 12, weight: .semibold))
                Text("Meetco")
                    .font(.system(size: 13, weight: .semibold))
            }
            if let topic = suggestions.meetingTopic {
                Text(topic)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                    .help("Detected meeting topic")
            }
        }
        .padding(.leading, 4)
    }

    @ViewBuilder
    private func section<Item: Identifiable, Row: View>(
        _ title: String,
        empty: String,
        items: [Item],
        @ViewBuilder row: @escaping (Item) -> Row
    ) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            SectionHeader(title: title, trailing: items.isEmpty ? nil : "\(items.count)")
                .padding(.bottom, 4)
            if items.isEmpty {
                if !empty.isEmpty {
                    Text(empty)
                        .font(.system(size: 11))
                        .foregroundStyle(.tertiary)
                        .padding(.horizontal, 4)
                }
            } else {
                ForEach(items) { row($0) }
            }
        }
    }
}

struct SidebarActionRow: View {
    let title: String
    let systemImage: String
    var isBusy = false
    let action: () -> Void

    @State private var isHovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 8) {
                Image(systemName: systemImage)
                    .font(.system(size: 12))
                    .frame(width: 16)
                    .foregroundStyle(.secondary)
                Text(title)
                    .font(.system(size: 12.5, weight: .medium))
                Spacer(minLength: 0)
                if isBusy { ProgressView().controlSize(.mini) }
            }
            .padding(.horizontal, 6)
            .padding(.vertical, 6)
            .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(isHovering ? Theme.hoverFill : .clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { isHovering = $0 }
    }
}

struct SidebarTextRow: View {
    let text: String
    let systemImage: String
    let help: String
    var isSelected = false
    let action: () -> Void

    @State private var isHovering = false

    var body: some View {
        Button(action: action) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Image(systemName: systemImage)
                    .font(.system(size: 10))
                    .foregroundStyle(.tertiary)
                    .frame(width: 16)
                Text(text)
                    .font(.system(size: 11.5))
                    .foregroundStyle(isSelected ? .primary : .secondary)
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 6)
            .padding(.vertical, 5)
            .background(
                RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .fill(isSelected ? Theme.selectedFill : (isHovering ? Theme.hoverFill : .clear))
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(help)
        .onHover { isHovering = $0 }
    }
}

private struct MemoryFact: Identifiable {
    let text: String
    var id: String { text }
}

/// A participant: name, then role and organization.
struct PersonRow: View {
    let person: MeetingParticipant

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Image(systemName: "person.fill")
                .font(.system(size: 9))
                .foregroundStyle(.tertiary)
                .frame(width: 16)
            VStack(alignment: .leading, spacing: 1) {
                Text(person.name)
                    .font(.system(size: 11.5, weight: .medium))
                    .lineLimit(1)
                if let detail = person.detail {
                    Text(detail)
                        .font(.system(size: 10.5))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 6)
        .padding(.vertical, 4)
        .help([person.detail, person.notes].compactMap { $0 }.joined(separator: "\n"))
    }
}
