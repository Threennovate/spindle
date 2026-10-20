import AppKit
import SwiftUI
import Testing
import SpindleKit
@testable import Spindle

@MainActor
struct NavigationLayoutTests {
    @Test func agentLogsOfferFitsTheWindowsMinimumHeight() throws {
        let suite = "com.spindletrack.layout-tests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let model = AppModel(tokens: MemoryTokenStore(), defaults: defaults)
        let settings = DesktopSettings(defaults: defaults, clearActivityCache: {})
        let desktop = Desktop(model: model, settings: settings, active: false)
        let controller = NSHostingController(rootView:
            NavigationSplitView {
                Text("Sidebar")
                    .navigationSplitViewColumnWidth(min: 200, ideal: 220, max: 300)
            } detail: {
                VStack(spacing: 0) {
                    AgentLogsOffer()
                        .padding(20)
                    Spacer()
                }
            }
            .frame(minWidth: 900, minHeight: 560)
            .environment(desktop)
        )

        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 900, height: 560),
                              styleMask: [.titled, .resizable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.isRestorable = false
        defer { window.close() }
        window.contentViewController = controller
        window.setContentSize(NSSize(width: 900, height: 560))
        window.layoutIfNeeded()
        controller.view.layoutSubtreeIfNeeded()

        let split = try #require(findSplit(in: controller.view))
        let rect = split.convert(split.bounds, to: controller.view)
        #expect(rect.minY >= -1)
        #expect(rect.maxY <= controller.view.bounds.height + 1)
    }

    private func findSplit(in view: NSView) -> NSSplitView? {
        if let split = view as? NSSplitView { return split }
        return view.subviews.lazy.compactMap { findSplit(in: $0) }.first
    }
}
