import AppKit
import Carbon.HIToolbox

/// Registers system-wide keyboard shortcuts with the Carbon hot key API.
/// This works while other apps (Meet, Teams) are focused and needs no Accessibility permission.
@MainActor
final class HotKeyManager {
    var onAction: ((ShortcutAction) -> Void)?

    private var hotKeyRefs: [UInt32: EventHotKeyRef] = [:]
    private var handlerRef: EventHandlerRef?
    private static let signature: OSType = 0x4D54_434F // "MTCO"

    func registerAll() {
        installHandlerIfNeeded()
        for action in ShortcutAction.allCases where hotKeyRefs[action.rawValue] == nil {
            var ref: EventHotKeyRef?
            let id = EventHotKeyID(signature: Self.signature, id: action.rawValue)
            let status = RegisterEventHotKey(action.keyCode, ShortcutAction.modifiers, id, GetApplicationEventTarget(), 0, &ref)
            if status == noErr, let ref {
                hotKeyRefs[action.rawValue] = ref
            } else {
                Log.app.error("Could not register shortcut \(action.displayString, privacy: .public) (status \(status))")
            }
        }
    }

    func unregisterAll() {
        for ref in hotKeyRefs.values { UnregisterEventHotKey(ref) }
        hotKeyRefs.removeAll()
    }

    fileprivate func handle(id: UInt32) {
        guard let action = ShortcutAction(rawValue: id) else { return }
        onAction?(action)
    }

    private func installHandlerIfNeeded() {
        guard handlerRef == nil else { return }
        var eventType = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let context = Unmanaged.passUnretained(self).toOpaque()
        InstallEventHandler(GetApplicationEventTarget(), { _, event, userData in
            guard let event, let userData else { return OSStatus(eventNotHandledErr) }
            var hotKeyID = EventHotKeyID()
            let status = GetEventParameter(
                event,
                EventParamName(kEventParamDirectObject),
                EventParamType(typeEventHotKeyID),
                nil,
                MemoryLayout<EventHotKeyID>.size,
                nil,
                &hotKeyID
            )
            guard status == noErr else { return status }
            let manager = Unmanaged<HotKeyManager>.fromOpaque(userData).takeUnretainedValue()
            let id = hotKeyID.id
            MainActor.assumeIsolated { manager.handle(id: id) }
            return noErr
        }, 1, &eventType, context, &handlerRef)
    }
}
