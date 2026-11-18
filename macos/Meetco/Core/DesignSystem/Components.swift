import SwiftUI

/// Compact icon-only button with a quiet hover state.
struct IconButton: View {
    let systemName: String
    let help: String
    var isActive = false
    var size: CGFloat = 28
    var tint: Color? = nil
    let action: () -> Void

    @State private var isHovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: systemName)
                .font(.system(size: size * 0.46, weight: .medium))
                .foregroundStyle(tint ?? (isActive ? Color.primary : Color.primary.opacity(0.78)))
                .frame(width: size, height: size)
                .background(
                    RoundedRectangle(cornerRadius: Theme.controlCornerRadius, style: .continuous)
                        .fill(isActive ? Theme.selectedFill : (isHovering ? Theme.hoverFill : .clear))
                )
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(help)
        .accessibilityLabel(help)
        .onHover { isHovering = $0 }
        .animation(.easeOut(duration: 0.12), value: isHovering)
    }
}

/// Small text button used in card footers.
struct QuietButton: View {
    let title: String
    let systemImage: String
    let action: () -> Void

    @State private var isHovering = false

    var body: some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
                .font(.system(size: 12, weight: .medium))
                .labelStyle(.titleAndIcon)
                .padding(.horizontal, 9)
                .padding(.vertical, 5)
                .background(
                    Capsule(style: .continuous).fill(isHovering ? Theme.hoverFill : Theme.subtleFill)
                )
                .overlay(Capsule(style: .continuous).strokeBorder(Theme.hairline, lineWidth: 0.5))
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .onHover { isHovering = $0 }
        .animation(.easeOut(duration: 0.12), value: isHovering)
    }
}

/// Tiny uppercase tag (language codes, badges).
struct TagLabel: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.system(size: 9.5, weight: .semibold, design: .rounded))
            .foregroundStyle(.secondary)
            .padding(.horizontal, 5)
            .padding(.vertical, 1.5)
            .background(Capsule(style: .continuous).fill(Theme.subtleFill))
            .overlay(Capsule(style: .continuous).strokeBorder(Theme.hairline, lineWidth: 0.5))
    }
}

/// Section header in small caps style.
struct SectionHeader: View {
    let title: String
    var trailing: String? = nil

    var body: some View {
        HStack {
            Text(title.uppercased())
                .font(Theme.sectionFont)
                .tracking(0.6)
                .foregroundStyle(.tertiary)
            Spacer()
            if let trailing {
                Text(trailing).font(.system(size: 10)).foregroundStyle(.tertiary)
            }
        }
    }
}

/// Pulsing dot that indicates active audio capture.
struct RecordingDot: View {
    var isActive: Bool
    @State private var pulse = false

    var body: some View {
        Circle()
            .fill(isActive ? Theme.recording : Color.secondary.opacity(0.5))
            .frame(width: 7, height: 7)
            .opacity(isActive && pulse ? 0.45 : 1)
            .animation(isActive ? .easeInOut(duration: 0.9).repeatForever(autoreverses: true) : .default, value: pulse)
            .onAppear { pulse = true }
            .accessibilityLabel(isActive ? "Listening" : "Not listening")
    }
}

/// Minimal five-bar level meter.
struct LevelMeter: View {
    var level: Float
    var isEnabled = true

    var body: some View {
        HStack(alignment: .center, spacing: 1.5) {
            ForEach(0..<5, id: \.self) { index in
                let threshold = Float(index) / 5
                RoundedRectangle(cornerRadius: 1)
                    .fill(Color.primary.opacity(isEnabled && level > threshold ? 0.75 : 0.15))
                    .frame(width: 2.5, height: 4 + CGFloat(index) * 2)
            }
        }
        .frame(height: 14)
        .animation(.linear(duration: 0.08), value: level)
        .accessibilityHidden(true)
    }
}

/// Elapsed time label for a running session.
struct ElapsedTimeText: View {
    let start: Date

    var body: some View {
        TimelineView(.periodic(from: start, by: 1)) { context in
            Text(Self.format(context.date.timeIntervalSince(start)))
                .monospacedDigit()
        }
    }

    static func format(_ interval: TimeInterval) -> String {
        let total = max(0, Int(interval))
        let hours = total / 3600, minutes = (total % 3600) / 60, seconds = total % 60
        return hours > 0
            ? String(format: "%d:%02d:%02d", hours, minutes, seconds)
            : String(format: "%02d:%02d", minutes, seconds)
    }
}

/// Animated "thinking" indicator for in-flight AI requests.
struct ThinkingIndicator: View {
    var label: String = "Thinking…"

    var body: some View {
        HStack(spacing: 8) {
            ProgressView().controlSize(.small).scaleEffect(0.7)
            Text(label).font(.system(size: 12)).foregroundStyle(.secondary)
        }
    }
}

/// Inline, dismissible error/warning banner.
struct InlineBanner: View {
    enum Style { case error, info }
    let message: String
    var style: Style = .error
    var onDismiss: (() -> Void)?

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: style == .error ? "exclamationmark.triangle" : "info.circle")
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
            Text(message)
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .textSelection(.enabled)
            Spacer(minLength: 0)
            if let onDismiss {
                Button(action: onDismiss) {
                    Image(systemName: "xmark").font(.system(size: 9, weight: .semibold))
                }
                .buttonStyle(.plain)
                .foregroundStyle(.tertiary)
                .help("Dismiss")
            }
        }
        .padding(10)
        .cardSurface(cornerRadius: 10)
    }
}
