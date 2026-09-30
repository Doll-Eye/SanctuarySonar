import Carbon.HIToolbox
import Foundation
import Keel

/// System-wide keys, so the recorder can be driven while the stream is in front.
///
/// Carbon's RegisterEventHotKey needs no permission, unlike an event tap. Whether a key gets
/// here while a streaming app has the keyboard depends on that app: one that captures system
/// shortcuts to send to the remote PC may take these first. Every press is logged, so the
/// log answers that for each app.
enum HotKeys {
    private static var actions: [UInt32: (String, () -> Void)] = [:]
    private static var installed = false
    private static var refs: [EventHotKeyRef] = []

    /// Control-Shift-Command: VoiceOver owns Control-Option, and nothing in macOS uses this
    /// combination with these letters.
    static let modifiers = UInt32(controlKey | shiftKey | cmdKey)

    static func register(_ name: String, keyCode: Int, _ action: @escaping () -> Void) {
        installIfNeeded()
        let id = UInt32(actions.count + 1)
        actions[id] = (name, action)
        var ref: EventHotKeyRef?
        let status = RegisterEventHotKey(UInt32(keyCode), modifiers,
                                         EventHotKeyID(signature: OSType(0x44474B59), id: id),  // 'DGKY'
                                         GetApplicationEventTarget(), 0, &ref)
        if status == noErr, let ref {
            refs.append(ref)
        } else {
            log("Hot key \(name) not registered: \(status)")
        }
    }

    private static func installIfNeeded() {
        guard !installed else { return }
        installed = true
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            var hotKey = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                              nil, MemoryLayout<EventHotKeyID>.size, nil, &hotKey)
            let id = hotKey.id
            DispatchQueue.main.async {
                guard let (name, action) = HotKeys.actions[id] else { return }
                log("Hot key: \(name)")
                action()
            }
            return noErr
        }, 1, &spec, nil, nil)
    }
}
