import SwiftUI

/// Asks for a name, e.g. for a report to save or a client to add.
struct NameSheet: View {
    @Environment(\.dismiss) private var dismiss
    let title: String
    var placeholder = ""
    var initial = ""
    var saveLabel = "Save"
    let onSave: (String) async throws -> Void
    @State private var name = ""
    @State private var error: String?
    @State private var saving = false

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(title).font(.title2.weight(.semibold))
            FormField(label: "Name", error: error) {
                TextField(placeholder, text: $name)
            }
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(saving ? "Saving…" : saveLabel) {
                    let trimmed = name.trimmingCharacters(in: .whitespaces)
                    guard !trimmed.isEmpty else {
                        error = "Enter a name."
                        return
                    }
                    saving = true
                    Task {
                        defer { saving = false }
                        do {
                            try await onSave(trimmed)
                            dismiss()
                        } catch {
                            self.error = FormErrors(error, fields: ["name"]).fields["name"] ?? error.localizedDescription
                        }
                    }
                }
                .buttonStyle(.brand)
                .keyboardShortcut(.defaultAction)
                .disabled(saving)
            }
        }
        .padding(24)
        .frame(width: 400)
        .onAppear { name = initial }
    }
}
