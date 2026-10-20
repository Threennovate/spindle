import AppKit
import Carbon.HIToolbox

/// A key with modifiers, e.g. ⌃⌥⌘S.
struct KeyCombo: Codable, Hashable {
    /// The virtual key code, e.g. 1 for S on every keyboard layout's position.
    var keyCode: UInt32
    var modifiers: NSEvent.ModifierFlags

    init(keyCode: UInt32, modifiers: NSEvent.ModifierFlags) {
        self.keyCode = keyCode
        self.modifiers = modifiers.intersection([.control, .option, .shift, .command])
    }

    init(event: NSEvent) {
        self.init(keyCode: UInt32(event.keyCode), modifiers: event.modifierFlags)
    }

    private enum CodingKeys: String, CodingKey { case keyCode, modifiers }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.init(keyCode: try container.decode(UInt32.self, forKey: .keyCode), modifiers: NSEvent.ModifierFlags(rawValue: try container.decode(UInt.self, forKey: .modifiers)))
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(keyCode, forKey: .keyCode)
        try container.encode(modifiers.rawValue, forKey: .modifiers)
    }

    func hash(into hasher: inout Hasher) {
        hasher.combine(keyCode)
        hasher.combine(modifiers.rawValue)
    }

    /// A global shortcut needs Command, Control or Option, or it would take a plain key from every app.
    var isUsable: Bool {
        !modifiers.intersection([.command, .control, .option]).isEmpty
    }

    /// "⌃⌥⌘S", as menus write shortcuts.
    var display: String {
        var text = ""
        if modifiers.contains(.control) { text += "⌃" }
        if modifiers.contains(.option) { text += "⌥" }
        if modifiers.contains(.shift) { text += "⇧" }
        if modifiers.contains(.command) { text += "⌘" }
        return text + Self.keyName(keyCode)
    }

    private static func keyName(_ keyCode: UInt32) -> String {
        switch Int(keyCode) {
        case kVK_Return: return "↩"
        case kVK_Space: return "Space"
        case kVK_Tab: return "⇥"
        case kVK_Delete: return "⌫"
        case kVK_Escape: return "⎋"
        case kVK_LeftArrow: return "←"
        case kVK_RightArrow: return "→"
        case kVK_UpArrow: return "↑"
        case kVK_DownArrow: return "↓"
        default:
            break
        }
        // The function keys' codes aren't in order (F20 comes before F1), so they are looked up one by one.
        let functionKeys = [
            kVK_F1, kVK_F2, kVK_F3, kVK_F4, kVK_F5, kVK_F6, kVK_F7, kVK_F8, kVK_F9, kVK_F10,
            kVK_F11, kVK_F12, kVK_F13, kVK_F14, kVK_F15, kVK_F16, kVK_F17, kVK_F18, kVK_F19, kVK_F20,
        ]
        if let index = functionKeys.firstIndex(of: Int(keyCode)) {
            return "F\(index + 1)"
        }
        // The character the key makes on the current keyboard layout.
        guard let source = TISCopyCurrentKeyboardLayoutInputSource()?.takeRetainedValue(),
              let pointer = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData)
        else { return "?" }
        let data = Unmanaged<CFData>.fromOpaque(pointer).takeUnretainedValue() as Data
        var deadKeys: UInt32 = 0
        var length = 0
        var characters = [UniChar](repeating: 0, count: 4)
        let status = data.withUnsafeBytes { layout in
            UCKeyTranslate(
                layout.bindMemory(to: UCKeyboardLayout.self).baseAddress, UInt16(keyCode), UInt16(kUCKeyActionDisplay), 0,
                UInt32(LMGetKbdType()), OptionBits(kUCKeyTranslateNoDeadKeysBit), &deadKeys, characters.count, &length, &characters)
        }
        return status == noErr && length > 0 ? String(utf16CodeUnits: characters, count: length).uppercased() : "?"
    }

    var carbonModifiers: UInt32 {
        var flags: UInt32 = 0
        if modifiers.contains(.command) { flags |= UInt32(cmdKey) }
        if modifiers.contains(.option) { flags |= UInt32(optionKey) }
        if modifiers.contains(.control) { flags |= UInt32(controlKey) }
        if modifiers.contains(.shift) { flags |= UInt32(shiftKey) }
        return flags
    }
}

/// Shortcuts that work while another app is in front, through the Carbon hot key API, which needs no permission.
/// One lives as long as the app.
final class HotKeys {
    private var registered: [UInt32: (ref: EventHotKeyRef, action: () -> Void)] = [:]
    private var handler: EventHandlerRef?
    private var nextId: UInt32 = 1

    init() {
        var type = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let context = Unmanaged.passUnretained(self).toOpaque()
        InstallEventHandler(GetApplicationEventTarget(), { _, event, context in
            guard let event, let context else { return OSStatus(eventNotHandledErr) }
            var id = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil, MemoryLayout<EventHotKeyID>.size, nil, &id)
            let keys = Unmanaged<HotKeys>.fromOpaque(context).takeUnretainedValue()
            MainActor.assumeIsolated {
                keys.registered[id.id]?.action()
            }
            return noErr
        }, 1, &type, context, &handler)
    }

    /// Replaces every shortcut with these. Returns the ones macOS wouldn't take, e.g. because another app has them.
    @discardableResult
    func register(_ shortcuts: [(KeyCombo, () -> Void)]) -> [KeyCombo] {
        unregisterAll()
        var refused: [KeyCombo] = []
        for (combo, action) in shortcuts where combo.isUsable {
            let id = nextId
            nextId += 1
            var ref: EventHotKeyRef?
            let status = RegisterEventHotKey(combo.keyCode, combo.carbonModifiers, EventHotKeyID(signature: OSType(0x5454_524B), id: id), GetApplicationEventTarget(), 0, &ref)
            if status == noErr, let ref {
                registered[id] = (ref, action)
            } else {
                refused.append(combo)
            }
        }
        return refused
    }

    private func unregisterAll() {
        for (ref, _) in registered.values {
            UnregisterEventHotKey(ref)
        }
        registered = [:]
    }
}
