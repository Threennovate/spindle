import AppKit
import SwiftUI

/// A borderless text field that hands the arrow keys, Return and Escape to its owner, e.g. to move through a list of
/// suggestions while typing. SwiftUI's text field lets the field editor handle those keys first.
struct CommandTextField: NSViewRepresentable {
    enum Command {
        case up, down, submit, cancel
    }

    @Binding var text: String
    var placeholder: String
    var font: NSFont = .systemFont(ofSize: NSFont.systemFontSize)
    @Binding var isFocused: Bool
    /// Returns whether it handled the key; if not, the field does what it normally does.
    var onCommand: (Command) -> Bool = { _ in false }
    /// Typing ended: the field lost focus, or Return or Tab was pressed.
    var onEndEditing: () -> Void = {}
    /// More keys for a field that is the whole interface, e.g. the quick entry panel's. Returns whether it handled the
    /// key; if not, the field does what it normally does.
    var onKey: (Key) -> Bool = { _ in false }
    /// Where the caret is, in UTF-16 units, whenever the text or the selection changes.
    var onCaret: (Int) -> Void = { _ in }
    /// A caret position to put the caret at, e.g. after text was put in the field; cleared once done.
    var caretRequest: Binding<Int?> = .constant(nil)

    enum Key {
        case tab, backtab
        case left(shift: Bool), right(shift: Bool)
        /// Backspace with the caret at the very start and nothing selected.
        case deleteAtStart
        /// ⌘↩.
        case alternateSubmit
    }

    func makeNSView(context: Context) -> NSTextField {
        let field = NSTextField()
        field.isBezeled = false
        field.drawsBackground = false
        field.focusRingType = .none
        field.lineBreakMode = .byTruncatingTail
        field.usesSingleLineMode = true
        field.cell?.isScrollable = true
        field.cell?.wraps = false
        field.delegate = context.coordinator
        field.setContentHuggingPriority(.defaultLow, for: .horizontal)
        field.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        return field
    }

    func updateNSView(_ field: NSTextField, context: Context) {
        context.coordinator.parent = self
        if field.stringValue != text {
            field.stringValue = text
        }
        if let caret = caretRequest.wrappedValue {
            let coordinator = context.coordinator
            DispatchQueue.main.async {
                caretRequest.wrappedValue = nil
                if field.currentEditor() == nil { field.window?.makeFirstResponder(field) }
                guard let editor = field.currentEditor() else { return }
                editor.selectedRange = NSRange(location: min(caret, (field.stringValue as NSString).length), length: 0)
                coordinator.parent.onCaret(editor.selectedRange.location)
            }
        }
        field.placeholderString = placeholder
        field.font = font
        context.coordinator.watchCaret(of: field)
        guard let window = field.window else { return }
        let editing = field.currentEditor() != nil && window.firstResponder == field.currentEditor()
        if isFocused && !editing {
            // After this update, so SwiftUI isn't changed while it is drawing.
            let coordinator = context.coordinator
            DispatchQueue.main.async {
                window.makeFirstResponder(field)
                coordinator.watchCaret(of: field)
            }
        }
    }

    func makeCoordinator() -> Coordinator {
        Coordinator(self)
    }

    final class Coordinator: NSObject, NSTextFieldDelegate {
        var parent: CommandTextField

        init(_ parent: CommandTextField) {
            self.parent = parent
        }

        func controlTextDidChange(_ notification: Notification) {
            guard let field = notification.object as? NSTextField else { return }
            parent.text = field.stringValue
            if let editor = field.currentEditor() {
                parent.onCaret(editor.selectedRange.location)
            }
        }

        /// The field editor this field's caret is followed in, once it has one.
        private weak var watchedEditor: NSTextView?
        private var selectionObserver: NSObjectProtocol?

        /// Follows the caret as it moves without the text changing, e.g. with the arrow keys or a click.
        func watchCaret(of field: NSTextField) {
            guard let editor = field.currentEditor() as? NSTextView, editor !== watchedEditor else { return }
            selectionObserver.map(NotificationCenter.default.removeObserver)
            watchedEditor = editor
            selectionObserver = NotificationCenter.default.addObserver(forName: NSTextView.didChangeSelectionNotification, object: editor, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    guard let self, let editor = self.watchedEditor, editor.delegate === field else { return }
                    self.parent.onCaret(editor.selectedRange().location)
                }
            }
        }

        func controlTextDidBeginEditing(_ notification: Notification) {
            if let field = notification.object as? NSTextField {
                watchCaret(of: field)
            }
            if !parent.isFocused {
                parent.isFocused = true
            }
        }

        func controlTextDidEndEditing(_ notification: Notification) {
            if parent.isFocused {
                parent.isFocused = false
            }
            parent.onEndEditing()
        }

        func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
            if let key = key(for: selector, in: textView), parent.onKey(key) {
                return true
            }
            return switch selector {
            case #selector(NSResponder.moveUp(_:)): parent.onCommand(.up)
            case #selector(NSResponder.moveDown(_:)): parent.onCommand(.down)
            case #selector(NSResponder.insertNewline(_:)): parent.onCommand(.submit)
            case #selector(NSResponder.cancelOperation(_:)): parent.onCommand(.cancel)
            default: false
            }
        }

        private func key(for selector: Selector, in textView: NSTextView) -> Key? {
            switch selector {
            case #selector(NSResponder.insertTab(_:)): return .tab
            case #selector(NSResponder.insertBacktab(_:)): return .backtab
            case #selector(NSResponder.moveLeft(_:)): return .left(shift: false)
            case #selector(NSResponder.moveRight(_:)): return .right(shift: false)
            case #selector(NSResponder.moveLeftAndModifySelection(_:)): return .left(shift: true)
            case #selector(NSResponder.moveRightAndModifySelection(_:)): return .right(shift: true)
            case #selector(NSResponder.deleteBackward(_:)):
                return textView.selectedRange() == NSRange(location: 0, length: 0) ? .deleteAtStart : nil
            case #selector(NSResponder.insertNewline(_:)), #selector(NSResponder.insertLineBreak(_:)), NSSelectorFromString("noop:"):
                return NSApp.currentEvent?.modifierFlags.contains(.command) == true && NSApp.currentEvent?.keyCode == 36 ? .alternateSubmit : nil
            default:
                return nil
            }
        }
    }
}
