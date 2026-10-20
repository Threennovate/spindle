import Combine
import Foundation
import Observation
import Sparkle

/// New versions of the app, through Sparkle. The feed is the server's `/downloads/spindle-mac/appcast.xml` (SUFeedURL in
/// Info.plist), where a release puts it (see docs/releasing.md). Sparkle installs an update only if it is signed with the
/// private half of the key in Info.plist (SUPublicEDKey) and with the same Developer ID as the app.
@Observable
final class Updates {
    /// Whether this build can update itself: a release build made with the update key, with someone at the Mac. A
    /// debug build never does, or it would replace itself with the latest release.
    let isAvailable: Bool
    /// Whether an update check can start now, i.e. none is under way.
    private(set) var canCheck = false
    /// Whether Sparkle looks for a new version once a day on its own.
    var checksAutomatically = false {
        didSet {
            if controller?.updater.automaticallyChecksForUpdates != checksAutomatically {
                controller?.updater.automaticallyChecksForUpdates = checksAutomatically
            }
        }
    }

    @ObservationIgnored private var controller: SPUStandardUpdaterController?
    @ObservationIgnored private var watching: AnyCancellable?

    init(active: Bool) {
        #if DEBUG
        isAvailable = false
        #else
        let key = Bundle.main.object(forInfoDictionaryKey: "SUPublicEDKey") as? String ?? ""
        isAvailable = active && !key.isEmpty
        #endif
        guard isAvailable else { return }
        let controller = SPUStandardUpdaterController(startingUpdater: true, updaterDelegate: nil, userDriverDelegate: nil)
        self.controller = controller
        checksAutomatically = controller.updater.automaticallyChecksForUpdates
        watching = controller.updater.publisher(for: \.canCheckForUpdates).sink { [weak self] can in
            self?.canCheck = can
        }
    }

    /// "Check for Updates…": shows what Sparkle finds, including that this is the latest version.
    func checkForUpdates() {
        controller?.checkForUpdates(nil)
    }
}
