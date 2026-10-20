import SwiftUI
import SpindleKit

/// Picks any number of tags. Typing narrows the list; a name that doesn't exist yet can be created.
struct TagPicker: View {
    let tracker: Tracker
    let tagIds: [String]
    let onChange: ([String]) -> Void

    @Environment(\.dismiss) private var dismiss
    @Environment(Notices.self) private var notices
    @State private var search = ""
    @State private var searching = true
    @State private var highlighted: String?
    @State private var creating = false

    private var name: String {
        search.trimmingCharacters(in: .whitespaces)
    }

    private var canCreate: Bool {
        guard let settings = tracker.settings else { return false }
        return tracker.isAdmin || !settings.onlyAdminsCreateTags
    }

    private var tags: [Tag] {
        tracker.catalog.sortedTags.filter { name.isEmpty || $0.name.localizedCaseInsensitiveContains(name) }
    }

    private var offersCreate: Bool {
        canCreate && !name.isEmpty && !tracker.catalog.tags.contains { $0.name.caseInsensitiveCompare(name) == .orderedSame }
    }

    /// What Return and the arrow keys move through: the tags, then creating one.
    private var choices: [String] {
        tags.map(\.id) + (offersCreate ? ["create"] : [])
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass")
                    .foregroundStyle(.secondary)
                CommandTextField(
                    text: $search,
                    placeholder: canCreate ? "Search or create tags" : "Search tags",
                    isFocused: $searching,
                    onCommand: handle)
            }
            .padding(10)
            Divider()
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 1) {
                        ForEach(tags) { tag in
                            row(id: tag.id, checked: tagIds.contains(tag.id)) {
                                Text(tag.name).lineLimit(1)
                            }
                        }
                        if offersCreate {
                            row(id: "create", checked: false) {
                                Label("Create tag “\(name)”", systemImage: "plus")
                            }
                            .disabled(creating)
                        }
                        if choices.isEmpty {
                            Text(name.isEmpty ? (canCreate ? "No tags yet. Type a name to create one." : "No tags yet.") : "No matching tags.")
                                .foregroundStyle(.secondary)
                                .padding(12)
                        }
                    }
                    .padding(5)
                }
                .onChange(of: highlighted) { _, id in
                    if let id { proxy.scrollTo(id) }
                }
            }
        }
        .frame(width: 280, height: 320)
        .onChange(of: search) {
            highlighted = nil
        }
    }

    private func row(id: String, checked: Bool, @ViewBuilder label: () -> some View) -> some View {
        Button {
            choose(id)
        } label: {
            HStack {
                label()
                Spacer(minLength: 8)
                if checked {
                    Image(systemName: "checkmark")
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(Color.brandText)
                }
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .contentShape(Rectangle())
            .background(highlighted == id ? Color.highlight : .clear, in: RoundedRectangle(cornerRadius: 5))
        }
        .buttonStyle(.plain)
        .id(id)
        .onHover { inside in
            if inside { highlighted = id }
        }
    }

    private func handle(_ command: CommandTextField.Command) -> Bool {
        let index = choices.firstIndex { $0 == highlighted }
        switch command {
        case .down where !choices.isEmpty:
            highlighted = choices[min((index ?? -1) + 1, choices.count - 1)]
        case .up where !choices.isEmpty:
            highlighted = choices[max((index ?? choices.count) - 1, 0)]
        case .submit:
            if let id = index.map({ choices[$0] }) ?? (choices.count == 1 ? choices.first : nil) {
                choose(id)
            }
        case .cancel:
            dismiss()
        default:
            break
        }
        return true
    }

    /// Tags toggle, and the list stays open to pick more.
    private func choose(_ id: String) {
        guard id == "create" else {
            onChange(tagIds.contains(id) ? tagIds.filter { $0 != id } : tagIds + [id])
            return
        }
        creating = true
        Task {
            defer { creating = false }
            do {
                let tag = try await tracker.createTag(named: name)
                onChange(tagIds + [tag.id])
                search = ""
            } catch {
                notices.show(error, fallback: "Couldn't create the tag.")
            }
        }
    }
}
