import Observation
import SwiftUI

/// A short message at the bottom of the window, e.g. that an entry was deleted or couldn't be saved, sometimes with
/// a button, e.g. "Split saved · Undo".
struct Notice: Identifiable, Equatable {
    enum Kind { case success, error }

    /// A button in the notice, e.g. Undo. Clicking it hides the notice.
    struct Action {
        let title: String
        let perform: () -> Void
    }

    let id = UUID()
    let text: String
    let kind: Kind
    var action: Action?

    static func == (lhs: Notice, rhs: Notice) -> Bool { lhs.id == rhs.id }
}

/// Shows one notice at a time, for a few seconds.
@Observable
final class Notices {
    private(set) var current: Notice?
    @ObservationIgnored private var hiding: Task<Void, Never>?

    func show(_ text: String, kind: Notice.Kind = .success, action: Notice.Action? = nil) {
        let notice = Notice(text: text, kind: kind, action: action)
        current = notice
        hiding?.cancel()
        // A notice with a button stays longer, so there is time to reach it.
        let seconds = action != nil ? 8 : kind == .error ? 6 : 3
        hiding = Task { [weak self] in
            try? await Task.sleep(for: .seconds(seconds))
            guard !Task.isCancelled, self?.current == notice else { return }
            self?.current = nil
        }
    }

    /// Shows what went wrong: the server's words, or `fallback` when it gave none.
    func show(_ error: Error, fallback: String) {
        let message = error.localizedDescription
        show(message.isEmpty ? fallback : message, kind: .error)
    }

    /// Clicks the current notice's button, and hides it.
    func perform() {
        let action = current?.action
        current = nil
        action?.perform()
    }

    func dismiss() {
        current = nil
    }
}

struct NoticeView: View {
    let notice: Notice
    var perform: () -> Void = {}
    let dismiss: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: notice.kind == .error ? "exclamationmark.triangle.fill" : "checkmark.circle.fill")
                .foregroundStyle(notice.kind == .error ? Color.red : Color.brandText)
            Text(notice.text)
                .fixedSize(horizontal: false, vertical: true)
            if let action = notice.action {
                Button(action.title, action: perform)
                    .buttonStyle(.borderless)
                    .fontWeight(.semibold)
                    .foregroundStyle(Color.brandText)
            }
            Button {
                dismiss()
            } label: {
                Image(systemName: "xmark")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Dismiss")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .frame(maxWidth: 480)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(.separator))
        .shadow(color: .black.opacity(0.12), radius: 10, y: 4)
        .accessibilityElement(children: .contain)
    }
}
