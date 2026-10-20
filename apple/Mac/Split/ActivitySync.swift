import Foundation
import Observation
import SpindleKit

/// Keeps the person's account up to date with what agent activity gives this Mac: the projects they chose for
/// repositories and the entries they kept as they are, always; and, when they turned on sync, the activity itself,
/// so the web can suggest splits too. Prompts, answers and folder paths never leave the Mac.
@Observable
final class ActivitySync {
    private let model: AppModel
    private let splits: SplitAdvisor
    private let settings: DesktopSettings
    private let defaults: UserDefaults
    @ObservationIgnored private let uploader: ActivityUploader
    /// Why the last upload failed, to show in settings; nil after one worked.
    private(set) var problem: String?

    @ObservationIgnored private var working = false
    @ObservationIgnored private var pulledRevision = -1
    @ObservationIgnored private var lastPull = Date.distantPast
    @ObservationIgnored private var lastUpload = Date.distantPast
    @ObservationIgnored private var uploadedRevision = -1
    @ObservationIgnored private var uploadWanted = true
    @ObservationIgnored private var wasSyncing: Bool?

    /// Uploads at most this often while agents write their logs, and at least this often otherwise.
    private static let uploadInterval: TimeInterval = 5 * 60
    private static let quietUploadInterval: TimeInterval = 30 * 60
    /// The account's links are read again this often anyway, in case a change was missed.
    private static let pullInterval: TimeInterval = 15 * 60

    init(model: AppModel, splits: SplitAdvisor, settings: DesktopSettings, defaults: UserDefaults = .standard,
         uploader: ActivityUploader = ActivityUploader(file: ActivityUploader.defaultFile)) {
        self.model = model
        self.splits = splits
        self.settings = settings
        self.defaults = defaults
        self.uploader = uploader
    }

    /// Whether this person's Macs upload agent activity, as their account says.
    var isSyncing: Bool { model.me?.agentActivity?.sync ?? false }
    var includesTitles: Bool { model.me?.agentActivity?.titles ?? false }

    /// A stable name for this Mac, so its sessions are told apart from another Mac's.
    private var deviceId: String {
        if let id = defaults.string(forKey: "agentActivityDevice") { return id }
        let id = UUID().uuidString.lowercased()
        defaults.set(id, forKey: "agentActivityDevice")
        return id
    }

    /// Uploads soon, e.g. once the timer stopped, so the web sees the entry's activity.
    func uploadSoon() {
        uploadWanted = true
    }

    /// Called every few seconds with the agents' revision: sends and reads what is due.
    func tick(revision: Int) {
        guard model.phase == .signedIn, model.me != nil, ActivitySnapshot.root == nil, !working else { return }
        let now = Date()
        if wasSyncing == true && !isSyncing {
            // Turned off, here or elsewhere: the server deleted what was sent, so it all goes again next time.
            Task { await uploader.reset() }
        }
        wasSyncing = isSyncing
        let pull = splits.isEnabled && (model.agentActivityRevision != pulledRevision || now.timeIntervalSince(lastPull) > Self.pullInterval
            || !splits.unsentLinks.isEmpty || !splits.unsentDismissals.isEmpty)
        let sinceUpload = now.timeIntervalSince(lastUpload)
        let upload = isSyncing && splits.isEnabled && !settings.activitySources.isEmpty
            && ((uploadWanted || revision != uploadedRevision) && sinceUpload > Self.uploadInterval || sinceUpload > Self.quietUploadInterval)
        guard pull || upload else { return }
        working = true
        Task {
            if pull { await self.pull() }
            if upload { await self.upload(revision: revision) }
            working = false
        }
    }

    /// Turns uploading on or off for all of the person's Macs. Off deletes what was uploaded.
    func change(sync: Bool, titles: Bool) async throws {
        _ = try await model.client.updateAgentActivitySettings(sync: sync, titles: sync && titles)
        try await model.refreshAccount()
        if !sync { await uploader.reset() }
        problem = nil
        uploadWanted = true
        lastUpload = .distantPast
    }

    // MARK: - Links and kept entries

    private func pull() async {
        let client = model.client
        let tracker = model.tracker
        let revision = model.agentActivityRevision
        for (identity, link) in splits.unsentLinks {
            // A link goes through a workspace that has the project; one of another workspace waits until it is open.
            // No project goes through any.
            guard let tracker, link.projectId.map({ tracker.catalog.project($0) != nil }) ?? true else { continue }
            do {
                try await client.linkRepository(identity: identity, name: link.name, to: link.projectId, noProject: link.noProject == true,
                                                in: tracker.workspace.id, branchRules: link.rules)
                splits.sent(link: identity, projectId: link.projectId, rules: link.rules, noProject: link.noProject)
            } catch let error as APIError where error.status == 400 {
                // The project can't be linked there, e.g. it was archived: keep it here only.
                splits.sent(link: identity, projectId: link.projectId, rules: link.rules, noProject: link.noProject)
            } catch {}
        }
        for (entryId, workspaceId) in splits.unsentDismissals {
            do {
                try await client.dismissSplitSuggestion(entryId: entryId, in: workspaceId)
                splits.sent(dismissal: entryId)
            } catch let error as APIError where error.status == 404 {
                splits.sent(dismissal: entryId)
            } catch {}
        }
        do {
            let links = try await client.repositoryLinks()
            splits.absorb(links)
            splits.absorb(dismissals: try await client.dismissedSplitSuggestions())
            pulledRevision = revision
            lastPull = Date()
        } catch {
            lastPull = Date()
        }
    }

    // MARK: - Activity

    private func upload(revision: Int) async {
        guard let account = model.me?.agentActivity, account.sync, let since = account.syncedSince else { return }
        let client = model.client, device = deviceId, sources = settings.activitySources
        let accountId = model.me?.id
        uploadWanted = false
        do {
            let result = try await uploader.upload(
                syncedSince: since, titles: account.titles, sources: sources,
                sessions: { since in
                    let sessions = try await AgentActivityLoader.shared.sessions(since: since, sources: sources)
                    try await self.requireCurrentUpload(accountId: accountId, syncedSince: account.syncedSince,
                                                        titles: account.titles, sources: sources)
                    return sessions
                },
                send: { batch in
                    try await self.requireCurrentUpload(accountId: accountId, syncedSince: account.syncedSince,
                                                        titles: account.titles, sources: sources)
                    do {
                        try await client.uploadAgentSessions(batch, device: device)
                    } catch let error as APIError where error.status == 400 {
                        throw ActivityUploadRejected()
                    }
                })
            uploadedRevision = revision
            problem = result.rejected > 0 ? "Some sessions couldn't be uploaded: the server refused them." : nil
        } catch is CancellationError {
            uploadWanted = true
            lastUpload = .distantPast
            return
        } catch let error as APIError where error.status == 409 {
            // Sync was turned off elsewhere.
            try? await model.refreshAccount()
        } catch {
            problem = (error as? LocalizedError)?.errorDescription ?? "The activity couldn't be uploaded."
        }
        lastUpload = Date()
    }

    /// An upload that was read under old consent, sources or account settings is no longer wanted.
    private func requireCurrentUpload(accountId: String?, syncedSince: Date?, titles: Bool, sources: Set<AgentSource>) throws {
        guard model.me?.id == accountId, let current = model.me?.agentActivity, current.sync,
              current.syncedSince == syncedSince, current.titles == titles,
              splits.isEnabled, settings.activitySources == sources else { throw CancellationError() }
    }

}
