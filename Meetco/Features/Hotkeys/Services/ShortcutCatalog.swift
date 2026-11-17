import Carbon.HIToolbox

enum ShortcutAction: UInt32, CaseIterable, Identifiable {
    case toggleListening = 1
    case whatShouldISay
    case recap
    case togglePanel
    case toggleCompact
    case toggleEverything

    var id: UInt32 { rawValue }

    var title: String {
        switch self {
        case .toggleListening: "Start / stop listening"
        case .whatShouldISay: "What should I say?"
        case .recap: "Recap so far"
        case .togglePanel: "Show / hide panel"
        case .toggleCompact: "Toggle compact mode"
        case .toggleEverything: "Show / hide Meetco"
        }
    }

    /// Carbon virtual key code. All shortcuts use Control + Option.
    var keyCode: UInt32 {
        switch self {
        case .toggleListening: UInt32(kVK_ANSI_L)
        case .whatShouldISay: UInt32(kVK_ANSI_A)
        case .recap: UInt32(kVK_ANSI_R)
        case .togglePanel: UInt32(kVK_ANSI_M)
        case .toggleCompact: UInt32(kVK_ANSI_C)
        case .toggleEverything: UInt32(kVK_ANSI_H)
        }
    }

    var keyLabel: String {
        switch self {
        case .toggleListening: "L"
        case .whatShouldISay: "A"
        case .recap: "R"
        case .togglePanel: "M"
        case .toggleCompact: "C"
        case .toggleEverything: "H"
        }
    }

    static let modifiers = UInt32(controlKey | optionKey)
    var displayString: String { "⌃⌥\(keyLabel)" }
}
