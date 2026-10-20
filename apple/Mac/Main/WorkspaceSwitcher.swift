import SwiftUI
import SpindleKit

/// Shows the current workspace and switches to another one, or creates one.
struct WorkspaceSwitcher: View {
    @Environment(AppModel.self) private var model
    @State private var creating = false
    @State private var failure: String?

    var body: some View {
        if let me = model.me, let workspace = model.workspace {
            Menu {
                Section("Your workspaces") {
                    ForEach(me.workspaces, id: \.id) { option in
                        Toggle(isOn: Binding(get: { option.id == workspace.id }, set: { _ in switchTo(option) })) {
                            Text(option.name)
                        }
                    }
                }
                if model.canCreateWorkspaces {
                    Divider()
                    Button("New Workspace…") {
                        creating = true
                    }
                }
            } label: {
                HStack(spacing: 8) {
                    VStack(alignment: .leading, spacing: 1) {
                        Text("Workspace")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        Text(workspace.name)
                            .font(.body.weight(.medium))
                            .lineLimit(1)
                            .truncationMode(.tail)
                    }
                    Spacer(minLength: 0)
                    Image(systemName: "chevron.up.chevron.down")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                .padding(.horizontal, 8)
                .padding(.vertical, 6)
                .contentShape(Rectangle())
            }
            .menuStyle(.button)
            .buttonStyle(.plain)
            .menuIndicator(.hidden)
            .background(.quaternary.opacity(0.6), in: RoundedRectangle(cornerRadius: 8))
            .accessibilityLabel("Workspace: \(workspace.name)")
            .sheet(isPresented: $creating) {
                NewWorkspaceSheet()
            }
            .alert("Couldn't switch workspaces", isPresented: Binding(get: { failure != nil }, set: { if !$0 { failure = nil } })) {
                Button("OK") {}
            } message: {
                Text(failure ?? "")
            }
        }
    }

    private func switchTo(_ option: WorkspaceSummary) {
        Task {
            do {
                try await model.switchWorkspace(to: option.id)
            } catch {
                failure = error.localizedDescription
            }
        }
    }
}

private struct NewWorkspaceSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var error: String?
    @State private var pending = false

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                Text("New workspace")
                    .font(.title2.weight(.semibold))
                Text("A separate space with its own projects, clients, tags and team. You become its admin.")
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            FormField(label: "Workspace name", error: error) {
                TextField("e.g. Consulting", text: $name)
            }
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(pending ? "Creating…" : "Create", action: create)
                    .keyboardShortcut(.defaultAction)
                    .disabled(pending)
            }
        }
        .padding(24)
        .frame(width: 420)
    }

    private func create() {
        let name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty else {
            error = "Enter a name."
            return
        }
        pending = true
        Task {
            defer { pending = false }
            do {
                try await model.createWorkspace(named: name)
                dismiss()
            } catch {
                self.error = FormErrors(error, fields: ["name"]).fields["name"] ?? error.localizedDescription
            }
        }
    }
}
