import SwiftUI
import SpindleKit

/// A labelled control with its error, if any, underneath, as on the web app's forms.
struct FormField<Content: View>: View {
    let label: String
    var error: String?
    var hint: String?
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(label)
                .font(.callout.weight(.medium))
            content
                .textFieldStyle(.roundedBorder)
                .controlSize(.large)
            if let error {
                Text(error)
                    .font(.callout)
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityLabel("Error: \(error)")
            } else if let hint {
                Text(hint)
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
    }
}

/// A message across the top of a form: what went wrong, or a notice.
struct FormBanner: View {
    enum Kind { case error, notice }
    let text: String
    var kind = Kind.error

    var body: some View {
        Label {
            Text(text)
                .fixedSize(horizontal: false, vertical: true)
        } icon: {
            Image(systemName: kind == .error ? "exclamationmark.triangle.fill" : "info.circle.fill")
                .foregroundStyle(kind == .error ? Color.red : Color.secondary)
        }
        .font(.callout)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(10)
        .background(
            kind == .error ? Color.red.opacity(0.1) : Color.primary.opacity(0.05),
            in: RoundedRectangle(cornerRadius: 8))
    }
}

/// Per-field messages, plus `form` for errors that belong to no single field.
struct FormErrors: Equatable {
    var fields: [String: String] = [:]
    var form: String?

    subscript(field: String) -> String? {
        get { fields[field] }
        set { fields[field] = newValue }
    }

    var isEmpty: Bool {
        form == nil && fields.values.allSatisfy { $0.isEmpty }
    }

    /// Turns a failed call into form errors: validation errors go next to their fields, anything else on top.
    init(_ error: Error, fields known: Set<String>) {
        let error = error.asSpindleError
        guard let problem = error as? APIError, problem.status == 400, !problem.fieldErrors.isEmpty else {
            form = error.localizedDescription
            return
        }
        for (field, messages) in problem.fieldErrors.sorted(by: { $0.key < $1.key }) {
            guard let message = messages.first else { continue }
            if known.contains(field) {
                fields[field] = fields[field] ?? message
            } else {
                form = form ?? message
            }
        }
    }

    init() {}
}

extension FormErrors {
    init(fields: [String: String]) {
        self.init()
        self.fields = fields
    }

    /// The details a finished entry is missing, next to their fields.
    init(missing: [(field: DetailField, message: String)]) {
        self.init()
        for item in missing {
            fields[item.field.rawValue] = item.message
        }
    }
}

enum Validation {
    static func email(_ email: String) -> String? {
        if email.isEmpty {
            return "Enter your email address."
        }
        return email.wholeMatch(of: /[^\s@]+@[^\s@]+\.[^\s@]+/) == nil ? "Enter a valid email address." : nil
    }

    static let passwordMinLength = 8
}
