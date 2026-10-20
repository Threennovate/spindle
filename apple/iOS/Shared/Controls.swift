import SwiftUI

/// The main action of a screen, full width in marigold with ink on it, in both modes.
struct ProminentButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.headline)
            .frame(maxWidth: .infinity, minHeight: 50)
            .foregroundStyle(Color.ink)
            .background(Color.brand.opacity(configuration.isPressed ? 0.8 : 1), in: Capsule())
            .opacity(isEnabled ? 1 : 0.5)
            .contentShape(Capsule())
    }
}

extension ButtonStyle where Self == ProminentButtonStyle {
    static var prominent: ProminentButtonStyle { ProminentButtonStyle() }
}

/// Another way to do a screen's main action, full width beside it: a quiet surface like the text fields', so the
/// marigold stays on the main one.
struct SecondaryButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.headline.weight(.medium))
            .frame(maxWidth: .infinity, minHeight: 50)
            .foregroundStyle(.primary)
            .background(Color(.secondarySystemGroupedBackground), in: Capsule())
            .overlay(Capsule().strokeBorder(Color.primary.opacity(0.08)))
            .opacity(configuration.isPressed ? 0.7 : isEnabled ? 1 : 0.5)
            .contentShape(Capsule())
    }
}

extension ButtonStyle where Self == SecondaryButtonStyle {
    static var secondary: SecondaryButtonStyle { SecondaryButtonStyle() }
}

/// A text field with its label above and its error below, on a rounded background, as iOS sign-in screens have them.
struct LabeledField<Field: View>: View {
    let label: String
    var error: String?
    var hint: String?
    @ViewBuilder var field: Field

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(label)
                .font(.subheadline.weight(.medium))
                .foregroundStyle(.secondary)
            field
                .padding(.horizontal, 14)
                .frame(minHeight: 50)
                .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 12))
                .overlay {
                    if error != nil {
                        RoundedRectangle(cornerRadius: 12).strokeBorder(.red.opacity(0.7))
                    }
                }
            if let error {
                Text(error)
                    .font(.footnote)
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityLabel("Error: \(error)")
            } else if let hint {
                Text(hint)
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
        }
    }
}
