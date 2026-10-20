import SwiftUI
import SpindleKit

/// The "What are you working on?" field. While typing it offers earlier entries; the arrow keys move through them,
/// Return starts the highlighted one or, with none highlighted, submits what was typed.
struct DescriptionField: View {
    let tracker: Tracker
    @Binding var text: String
    @Binding var isFocused: Bool
    let onSubmit: () -> Void
    let onPick: (TimeEntrySuggestion) -> Void

    @State private var suggestions: [TimeEntrySuggestion] = []
    /// Remembered by what it is, not by position: if the list changes as Return is pressed, what starts is still
    /// what was highlighted, or nothing.
    @State private var highlighted: String?
    @State private var showing = false

    var body: some View {
        CommandTextField(
            text: $text,
            placeholder: "What are you working on?",
            font: .systemFont(ofSize: 15),
            isFocused: $isFocused,
            onCommand: handle)
            .frame(maxWidth: .infinity, minHeight: 24)
            .accessibilityLabel("Description")
            .overlay(alignment: .topLeading) {
                if showing && !suggestions.isEmpty {
                    list
                        .offset(y: 34)
                }
            }
            .task(id: "\(text)|\(isFocused)") {
                guard isFocused else {
                    // Late, so a click on a suggestion lands before the list goes.
                    try? await Task.sleep(for: .milliseconds(200))
                    if !Task.isCancelled { showing = false }
                    return
                }
                showing = true
                try? await Task.sleep(for: .milliseconds(150))
                guard !Task.isCancelled, let found = try? await tracker.suggestions(matching: text) else { return }
                suggestions = found
                if !found.contains(where: { $0.id == highlighted }) {
                    highlighted = nil
                }
            }
            .onChange(of: text) {
                highlighted = nil
            }
    }

    private var list: some View {
        VStack(alignment: .leading, spacing: 1) {
            Text("Previous entries")
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
                .padding(.horizontal, 8)
                .padding(.top, 4)
            ForEach(suggestions) { suggestion in
                Button {
                    pick(suggestion)
                } label: {
                    SuggestionRow(suggestion: suggestion, catalog: tracker.catalog)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 6)
                        .contentShape(Rectangle())
                        .background(
                            highlighted == suggestion.id ? Color.highlight : .clear,
                            in: RoundedRectangle(cornerRadius: 6))
                }
                .buttonStyle(.plain)
                .onHover { inside in
                    if inside { highlighted = suggestion.id }
                }
            }
        }
        .padding(5)
        .frame(width: 560, alignment: .leading)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(.separator))
        .shadow(color: .black.opacity(0.15), radius: 12, y: 6)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Previous entries")
    }

    private func handle(_ command: CommandTextField.Command) -> Bool {
        let index = suggestions.firstIndex { $0.id == highlighted }
        switch command {
        case .down:
            showing = true
            guard !suggestions.isEmpty else { return true }
            highlighted = suggestions[min((index ?? -1) + 1, suggestions.count - 1)].id
        case .up:
            guard let index else { return true }
            highlighted = index == 0 ? nil : suggestions[index - 1].id
        case .cancel:
            if showing {
                showing = false
                highlighted = nil
            } else {
                isFocused = false
            }
        case .submit:
            if showing, let index {
                pick(suggestions[index])
            } else {
                showing = false
                onSubmit()
            }
        }
        return true
    }

    private func pick(_ suggestion: TimeEntrySuggestion) {
        showing = false
        highlighted = nil
        onPick(suggestion)
    }
}

private struct SuggestionRow: View {
    let suggestion: TimeEntrySuggestion
    let catalog: Catalog

    var body: some View {
        HStack(spacing: 12) {
            Text(suggestion.description.isEmpty ? "No description" : suggestion.description)
                .foregroundStyle(suggestion.description.isEmpty ? .secondary : .primary)
                .lineLimit(1)
            Spacer(minLength: 12)
            if let project = catalog.project(suggestion.projectId) {
                ProjectLabel(project: project, task: catalog.task(suggestion.taskId), client: catalog.client(project.clientId))
                    .font(.callout)
            }
        }
    }
}
