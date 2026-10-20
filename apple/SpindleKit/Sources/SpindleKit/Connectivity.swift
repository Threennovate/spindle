import Foundation
import Network
import Observation

/// Whether the device has a network at all, and when it comes back.
@MainActor
@Observable
public final class Connectivity {
    public private(set) var isOnline = true
    /// The network came back, e.g. to send what waited.
    @ObservationIgnored public var onOnline: (() -> Void)?

    private let monitor = NWPathMonitor()

    public init() {
        monitor.pathUpdateHandler = { [weak self] path in
            let online = path.status == .satisfied
            Task { @MainActor in self?.update(online) }
        }
        monitor.start(queue: DispatchQueue(label: "com.spindletrack.connectivity"))
    }

    deinit {
        monitor.cancel()
    }

    private func update(_ online: Bool) {
        guard online != isOnline else { return }
        isOnline = online
        if online {
            onOnline?()
        }
    }
}
