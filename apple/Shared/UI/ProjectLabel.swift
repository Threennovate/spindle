import SwiftUI
import SpindleKit

struct ProjectDot: View {
    let color: String
    var size: CGFloat = 9

    var body: some View {
        Circle()
            .fill(Color(hex: color))
            .frame(width: size, height: size)
            .accessibilityHidden(true)
    }
}

/// "● Website · Design · Acme": the project with its task and client.
struct ProjectLabel: View {
    let project: Project
    var task: ProjectTask?
    var client: WorkspaceClient?

    var body: some View {
        HStack(spacing: 6) {
            ProjectDot(color: project.color)
            Text(project.name)
                .fontWeight(.medium)
                .foregroundStyle(Color(hex: project.color))
                .lineLimit(1)
            if let task {
                Text("· \(task.name)")
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            if let client {
                Text("· \(client.name)")
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
        }
        .accessibilityElement(children: .combine)
    }
}

/// A tag's name in a small capsule.
struct TagChip: View {
    let name: String

    var body: some View {
        Text(name)
            .font(.caption)
            .padding(.horizontal, 7)
            .padding(.vertical, 2)
            .background(.quaternary, in: Capsule())
            .lineLimit(1)
    }
}
