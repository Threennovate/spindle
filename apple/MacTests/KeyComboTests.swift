import AppKit
import Carbon.HIToolbox
import Testing
@testable import Spindle

struct KeyComboTests {
    @Test func writesShortcutsAsMenusDo() {
        let combo = KeyCombo(keyCode: UInt32(kVK_ANSI_S), modifiers: [.control, .option, .command])
        #expect(combo.display.hasPrefix("⌃⌥⌘"))
        #expect(KeyCombo(keyCode: UInt32(kVK_F1), modifiers: [.command]).display == "⌘F1")
        #expect(KeyCombo(keyCode: UInt32(kVK_F20), modifiers: [.command]).display == "⌘F20")
        #expect(KeyCombo(keyCode: UInt32(kVK_Space), modifiers: [.option]).display == "⌥Space")
    }

    @Test func needsCommandControlOrOptionToWorkEverywhere() {
        #expect(KeyCombo(keyCode: UInt32(kVK_ANSI_S), modifiers: [.command]).isUsable)
        #expect(!KeyCombo(keyCode: UInt32(kVK_ANSI_S), modifiers: [.shift]).isUsable)
        #expect(!KeyCombo(keyCode: UInt32(kVK_ANSI_S), modifiers: []).isUsable)
    }

    @Test func keepsOnlyTheModifiersThatMatter() throws {
        let combo = KeyCombo(keyCode: 1, modifiers: [.command, .capsLock, .function])
        #expect(combo.modifiers == [.command])
        let saved = try JSONEncoder().encode(combo)
        #expect(try JSONDecoder().decode(KeyCombo.self, from: saved) == combo)
    }
}
