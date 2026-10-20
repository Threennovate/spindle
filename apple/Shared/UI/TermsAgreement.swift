import SwiftUI
import SpindleKit

/// Agreeing to the server's terms when creating an account, on a server that has them.
struct TermsAgreement: View {
    let termsUrl: String
    let privacyUrl: String?
    @Binding var accepted: Bool
    var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                // The sentence beside it has links, so it isn't the toggle's label: a click on a link opens it.
                Toggle("I agree to the terms of service", isOn: $accepted)
                    .labelsHidden()
                    #if os(macOS)
                    .toggleStyle(.checkbox)
                    #endif
                Text(sentence)
                    .font(.callout)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if let error {
                Text(error)
                    .font(.callout)
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityLabel("Error: \(error)")
            }
        }
    }

    private var sentence: AttributedString {
        var markdown = "I agree to the [terms of service](\(termsUrl))"
        if let privacyUrl {
            markdown += " and the [privacy policy](\(privacyUrl))"
        }
        return (try? AttributedString(markdown: markdown + ".")) ?? AttributedString("I agree to the terms of service.")
    }
}
