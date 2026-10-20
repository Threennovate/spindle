import AppKit
import SwiftUI

/// A floating panel that takes the keyboard without bringing Spindle to the front, like Spotlight: the app the
/// person works in stays active, and is where they are when the panel goes away.
final class QuickEntryPanel: NSPanel {
    /// ⌘↩ and ⌘Z, which a text field would otherwise not hand on.
    var onKeyEquivalent: (NSEvent) -> Bool = { _ in false }
    var onResignKey: () -> Void = {}

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        onKeyEquivalent(event) || super.performKeyEquivalent(with: event)
    }

    override func resignKey() {
        super.resignKey()
        onResignKey()
    }
}

/// Opens and closes the quick entry panel: over any app, on any Space and over full-screen apps, at the top third of
/// the screen the person is working on.
///
/// The window stays one height, clear, and the card in it grows and shrinks with what it shows. Resizing the window
/// to the card as the person typed made it jump, cut off its footer and left the vibrancy of its old size behind.
/// Clicks on the clear part go to the window below, which takes the panel away as a click outside it does.
final class QuickEntryController {
    let quick: QuickEntry
    private var panel: QuickEntryPanel?

    static let width: CGFloat = 640
    /// Room for the tallest card: the field, the running timer or a split, the longest list and the footer.
    static let height: CGFloat = 680
    static let cornerRadius: CGFloat = 16

    init(model: AppModel, desktop: Desktop) {
        quick = QuickEntry(model: model, desktop: desktop)
        quick.close = { [weak self] in self?.hide() }
    }

    var isVisible: Bool { panel?.isVisible == true }

    func toggle() {
        isVisible ? hide() : show()
    }

    func show() {
        let panel = panel ?? makePanel()
        self.panel = panel
        quick.reset()
        place(panel)
        panel.orderFrontRegardless()
        panel.makeKey()
    }

    func hide() {
        guard let panel, panel.isVisible else { return }
        panel.orderOut(nil)
    }

    private func makePanel() -> QuickEntryPanel {
        let panel = QuickEntryPanel(
            contentRect: NSRect(x: 0, y: 0, width: Self.width, height: Self.height),
            styleMask: [.nonactivatingPanel, .borderless, .fullSizeContentView],
            backing: .buffered, defer: true)
        panel.isFloatingPanel = true
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient, .ignoresCycle]
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.isMovableByWindowBackground = false
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.animationBehavior = .utilityWindow
        panel.setAccessibilityLabel("Spindle quick entry")

        let content = NSHostingView(rootView: QuickEntryView(quick: quick) { [weak panel] _ in
            // The window's shadow follows what it draws, so it is redrawn once the card has its new size.
            DispatchQueue.main.async { panel?.invalidateShadow() }
        }
        .background(Vibrancy(cornerRadius: Self.cornerRadius))
        .frame(maxHeight: .infinity, alignment: .top)
        .environment(quick.model)
        .environment(quick.desktop))
        // The window keeps its size whatever the card's.
        content.sizingOptions = []
        panel.contentView = content

        panel.onResignKey = { [weak self] in
            // A click outside, or another app in front: the panel goes, as Spotlight does.
            self?.hide()
        }
        panel.onKeyEquivalent = { [weak self] event in
            guard let self, event.modifierFlags.intersection(.deviceIndependentFlagsMask) == .command else { return false }
            switch event.charactersIgnoringModifiers {
            case "\r":
                quick.submitAlternate()
                return true
            case "z":
                quick.undo()
                return true
            case "w":
                hide()
                return true
            default:
                return false
            }
        }
        return panel
    }

    /// The top third of the screen with the mouse on it, which is where the person is working.
    private func place(_ panel: NSPanel) {
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) } ?? NSScreen.main ?? NSScreen.screens[0]
        let visible = screen.visibleFrame
        let top = visible.maxY - visible.height / 5
        let height = min(Self.height, top - visible.minY)
        panel.setFrame(NSRect(x: visible.midX - Self.width / 2, y: top - height, width: Self.width, height: height), display: false)
    }
}

/// The blur of what is behind the window, with round corners. A mask image rounds the blur itself, which a clip of
/// the view would not.
private struct Vibrancy: NSViewRepresentable {
    let cornerRadius: CGFloat

    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = .popover
        view.blendingMode = .behindWindow
        view.state = .active
        let side = cornerRadius * 2 + 1
        let mask = NSImage(size: NSSize(width: side, height: side), flipped: false) { rect in
            NSColor.black.setFill()
            NSBezierPath(roundedRect: rect, xRadius: cornerRadius, yRadius: cornerRadius).fill()
            return true
        }
        mask.capInsets = NSEdgeInsets(top: cornerRadius, left: cornerRadius, bottom: cornerRadius, right: cornerRadius)
        mask.resizingMode = .stretch
        view.maskImage = mask
        return view
    }

    func updateNSView(_ view: NSVisualEffectView, context: Context) {}
}
