import SwiftUI
import SpindleKit

extension View {
    /// Offers descriptions written before while one is typed in the entry editor, those on `projectId` first.
    /// Picking one only fills in the text; the entry's project, task and split stay as they are. Needs macOS 15;
    /// earlier versions keep a plain field.
    func descriptionSuggestions(_ tracker: Tracker, text: String, projectId: String?) -> some View {
        modifier(DescriptionSuggestions(tracker: tracker, text: text, projectId: projectId))
    }
}

private struct DescriptionSuggestions: ViewModifier {
    let tracker: Tracker
    let text: String
    let projectId: String?

    @State private var found: [String] = []

    func body(content: Content) -> some View {
        Group {
            if #available(macOS 15.0, *) {
                content.textInputSuggestions {
                    ForEach(found, id: \.self) { description in
                        Text(description).textInputCompletion(description)
                    }
                }
            } else {
                content
            }
        }
        .task(id: "\(text)|\(projectId ?? "")") {
            // Nothing is offered for an empty field, so opening the editor does not open a list.
            guard !text.trimmingCharacters(in: .whitespaces).isEmpty else {
                found = []
                return
            }
            try? await Task.sleep(for: .milliseconds(150))
            guard !Task.isCancelled, let descriptions = try? await tracker.descriptions(matching: text, projectId: projectId) else { return }
            found = descriptions
        }
    }
}
