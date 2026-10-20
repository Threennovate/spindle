import SwiftUI

/// The Spindle logo: the mark and the name, drawn from the branding files.
struct Logo: View {
    var height: CGFloat = 28

    var body: some View {
        Image("Logo")
            .resizable()
            .aspectRatio(contentMode: .fit)
            .frame(height: height)
            .accessibilityLabel("Spindle")
    }
}

/// The mark alone, for small spaces.
struct LogoMark: View {
    var size: CGFloat = 24

    var body: some View {
        Image("LogoMark")
            .resizable()
            .aspectRatio(contentMode: .fit)
            .frame(width: size, height: size)
            .accessibilityHidden(true)
    }
}

/// A person's initials in a circle, e.g. "AK" for Ana Kovač.
struct Avatar: View {
    let name: String
    var size: CGFloat = 28

    var body: some View {
        Text(Self.initials(of: name))
            .font(.system(size: size * 0.4, weight: .semibold))
            .foregroundStyle(.secondary)
            .frame(width: size, height: size)
            .background(.quaternary, in: Circle())
            .accessibilityHidden(true)
    }

    static func initials(of name: String) -> String {
        let words = name.split(whereSeparator: \.isWhitespace)
        let letters = [words.first, words.count > 1 ? words.last : nil].compactMap { $0?.first }
        return letters.isEmpty ? "?" : String(letters).uppercased()
    }
}

extension Color {
    /// Spindle's marigold, the colour of the middle bar of the mark: for fills such as the main button, the timer's
    /// start button and a selected split part, with `ink` on them. Marigold is too light to read on white, so text and
    /// symbols take `brandText`.
    static let brand = Color("Marigold")
    /// The accent of controls: a deeper amber in the marigold's family, which the system's white text on segmented
    /// controls, selections and switches stays readable on (4:1 in light mode, 3.4:1 in dark, like Apple's own blue).
    /// macOS uses it when the person keeps "Multicolor" in System Settings, and their own accent otherwise. The Mac
    /// windows don't tint with it, so plain buttons stay neutral, as in other Mac apps; the iPhone app does.
    static let control = Color("AccentColor")
    /// Text and symbols in the brand colour: bronze in light mode, where marigold would wash out, and marigold in dark.
    static let brandText = Color("BrandText")
    /// Graphite ink, for text and symbols on marigold in both modes.
    static let ink = Color(red: 0x14 / 255, green: 0x15 / 255, blue: 0x18 / 255)
}

extension Color {
    /// The row under the keyboard or pointer in a list of choices: a neutral tint, since a wash of marigold turns olive
    /// over graphite and beige over white.
    static let highlight = Color.primary.opacity(0.08)
}

extension View {
    /// A card that stands apart from the window without a wash of colour: a quiet raised surface and a hairline, the
    /// same over graphite and white. The brand shows in one detail inside it, such as its symbol or main button.
    func cardSurface(cornerRadius: CGFloat = 10) -> some View {
        background(Color.primary.opacity(0.045), in: RoundedRectangle(cornerRadius: cornerRadius))
            .overlay(RoundedRectangle(cornerRadius: cornerRadius).strokeBorder(Color.primary.opacity(0.1)))
    }
}

extension Color {
    /// A color written as `#RRGGBB`, e.g. a project's.
    init(hex: String) {
        let digits = hex.hasPrefix("#") ? String(hex.dropFirst()) : hex
        let value = UInt32(digits, radix: 16) ?? 0x808080
        self.init(
            .sRGB,
            red: Double((value >> 16) & 0xFF) / 255,
            green: Double((value >> 8) & 0xFF) / 255,
            blue: Double(value & 0xFF) / 255)
    }
}

/// The main action of a form or screen, in marigold with ink on it in both modes, as on the web app.
struct BrandButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled
    @Environment(\.controlSize) private var controlSize

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.body.weight(.medium))
            .padding(.vertical, controlSize == .large ? 8 : 4)
            .padding(.horizontal, controlSize == .large ? 16 : 10)
            .foregroundStyle(Color.ink)
            .background(
                Color.brand.opacity(configuration.isPressed ? 0.8 : 1),
                in: RoundedRectangle(cornerRadius: controlSize == .large ? 8 : 6))
            .opacity(isEnabled ? 1 : 0.55)
            .contentShape(Rectangle())
    }
}

extension ButtonStyle where Self == BrandButtonStyle {
    static var brand: BrandButtonStyle { BrandButtonStyle() }
}

/// A button that reads as a link, in the brand colour rather than the system's link blue.
struct BrandLinkButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .foregroundStyle(isEnabled ? Color.brandText : Color.secondary)
            .opacity(configuration.isPressed ? 0.7 : 1)
            .contentShape(Rectangle())
            .linkPointer()
    }
}

extension ButtonStyle where Self == BrandLinkButtonStyle {
    static var brandLink: BrandLinkButtonStyle { BrandLinkButtonStyle() }
}

extension View {
    /// The pointing hand over links, where macOS offers it.
    @ViewBuilder func linkPointer() -> some View {
        #if os(macOS)
        if #available(macOS 15, *) {
            pointerStyle(.link)
        } else {
            self
        }
        #else
        self
        #endif
    }
}
