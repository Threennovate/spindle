import AppKit
import SwiftUI
import SpindleKit

/// The field every clock time is typed in, so they all work alike, as on the web app:
///
/// - entering it selects the time, so typing replaces it
/// - it reads `930`, `9:30`, `9.30`, `9pm`, `now`, or `-15` for 15 minutes ago, and tidies that to `09:30` when left
/// - ↑ and ↓ change the time by a minute, with Shift by 15
/// - a time it can't read turns red once the field is left
struct ClockField: NSViewRepresentable {
    @Binding var text: String
    let time: UserTime
    var alignment: NSTextAlignment = .natural
    /// Set to focus the field, e.g. when a popover opens; cleared when the field is left.
    var isFocused: Binding<Bool>?
    /// The field was left or Return was pressed: the time typed, or nil if it isn't one.
    var onCommit: (ClockTime.Clock?) -> Void = { _ in }
    /// Return was pressed, after the time was read.
    var onSubmit: () -> Void = {}

    func makeNSView(context: Context) -> NSTextField {
        let field = SelectingTextField()
        field.bezelStyle = .roundedBezel
        field.isBezeled = true
        field.usesSingleLineMode = true
        field.lineBreakMode = .byClipping
        field.cell?.isScrollable = true
        field.cell?.wraps = false
        field.font = .monospacedDigitSystemFont(ofSize: NSFont.systemFontSize, weight: .regular)
        field.delegate = context.coordinator
        return field
    }

    func updateNSView(_ field: NSTextField, context: Context) {
        context.coordinator.parent = self
        if field.stringValue != text {
            field.stringValue = text
        }
        field.alignment = alignment
        field.placeholderString = time.hour12 ? "9:30 am" : "9:30"
        field.isEnabled = context.environment.isEnabled
        field.textColor = context.coordinator.unreadable ? .systemRed : .labelColor
        guard let window = field.window, isFocused?.wrappedValue == true else { return }
        if field.currentEditor() == nil || window.firstResponder != field.currentEditor() {
            // After this update, so SwiftUI isn't changed while it is drawing.
            DispatchQueue.main.async { window.makeFirstResponder(field) }
        }
    }

    func makeCoordinator() -> Coordinator {
        Coordinator(self)
    }

    final class Coordinator: NSObject, NSTextFieldDelegate {
        var parent: ClockField
        /// What was typed isn't a time, and the field was left.
        var unreadable = false

        init(_ parent: ClockField) {
            self.parent = parent
        }

        func controlTextDidChange(_ notification: Notification) {
            guard let field = notification.object as? NSTextField else { return }
            unreadable = false
            field.textColor = .labelColor
            parent.text = field.stringValue
        }

        func controlTextDidBeginEditing(_ notification: Notification) {
            if parent.isFocused?.wrappedValue == false {
                parent.isFocused?.wrappedValue = true
            }
        }

        func controlTextDidEndEditing(_ notification: Notification) {
            guard let field = notification.object as? NSTextField else { return }
            commit(field)
            if parent.isFocused?.wrappedValue == true {
                parent.isFocused?.wrappedValue = false
            }
        }

        func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
            guard let field = control as? NSTextField else { return false }
            switch selector {
            case #selector(NSResponder.moveUp(_:)): nudge(field, by: 1)
            case #selector(NSResponder.moveDown(_:)): nudge(field, by: -1)
            case #selector(NSResponder.moveUpAndModifySelection(_:)): nudge(field, by: 15)
            case #selector(NSResponder.moveDownAndModifySelection(_:)): nudge(field, by: -15)
            case #selector(NSResponder.insertNewline(_:)):
                commit(field)
                parent.onSubmit()
            default: return false
            }
            return true
        }

        private func commit(_ field: NSTextField) {
            let text = field.stringValue
            let clock = parent.time.readClockField(text)
            if let clock {
                set(field, ClockTime.field(clock, hour12: parent.time.hour12))
            }
            unreadable = clock == nil && !text.trimmingCharacters(in: .whitespaces).isEmpty
            field.textColor = unreadable ? .systemRed : .labelColor
            parent.onCommit(clock)
        }

        private func nudge(_ field: NSTextField, by minutes: Int) {
            guard let clock = parent.time.readClockField(field.stringValue) else { return }
            set(field, ClockTime.field(ClockTime.adding(minutes, to: clock), hour12: parent.time.hour12))
            // Stays selected, so typing still replaces the time.
            field.currentEditor()?.selectAll(nil)
        }

        private func set(_ field: NSTextField, _ text: String) {
            if field.stringValue != text {
                field.stringValue = text
            }
            if parent.text != text {
                parent.text = text
            }
        }
    }
}

/// A text field that selects its text whenever it is entered, also by a click, which would otherwise put the caret
/// where it landed.
private final class SelectingTextField: NSTextField {
    override func becomeFirstResponder() -> Bool {
        let became = super.becomeFirstResponder()
        if became {
            // After the click that entered the field has placed the caret.
            DispatchQueue.main.async { [weak self] in self?.currentEditor()?.selectAll(nil) }
        }
        return became
    }
}
