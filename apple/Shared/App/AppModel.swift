import Foundation
import Observation
import SpindleKit

/// What every window shares: the server, whether someone is signed in, and who.
@Observable
final class AppModel {
    enum Phase: Equatable {
        /// Picking up the session from the last launch.
        case starting
        case signedOut
        /// Signed in, but the account could not be loaded, e.g. the server is unreachable.
        case unavailable(String)
        case signedIn
    }

    private(set) var phase = Phase.starting
    /// The server the app talks to: Spindle Cloud, or the person's own, chosen while signed out.
    private(set) var server: URL
    private(set) var client: SpindleClient
    private(set) var me: Me? {
        didSet { syncTracker() }
    }
    /// Why sign-in is showing, when it is not the person's own doing.
    private(set) var signInNotice: String?
    /// What the server offers before signing in, e.g. the terms to agree to. Nil until it answers.
    private(set) var serverInfo: ServerInfo?

    /// The signed-in person's time in the current workspace.
    private(set) var tracker: Tracker?
    /// What this device keeps for the signed-in account: its cache, the changes waiting, and live sync.
    private(set) var account: Account?
    let connectivity = Connectivity()
    /// Asks whether to sign out although changes haven't reached the server.
    var confirmingSignOut = false
    /// Counts up when data may have changed elsewhere, for screens that load their own, e.g. reports.
    private(set) var dataRevision = 0
    /// Counts up when the person's repository links or kept suggestions changed on another device or the web.
    private(set) var agentActivityRevision = 0
    /// Counts the person's agent proposals changing on the server, for the inbox to read them again.
    private(set) var agentProposalsRevision = 0
    let notices = Notices()
    /// The entry open for editing, e.g. to add the details the timer needs to stop.
    var editing: EntryEditing?
    /// The part of the app the window shows.
    var section: AppSection {
        didSet { defaults.set(section.rawValue, forKey: "section") }
    }
    /// Set when the timer's description field should take focus, e.g. for the New Entry command; the field clears it.
    var wantsDescriptionFocus = false
    /// The running timer's duration for the menu bar, e.g. "0:47:12", updated every second.
    private(set) var menuBarClock: String?
    /// What screens keep between visits, by kind, person and workspace, e.g. the last report. Forgotten on signing out.
    @ObservationIgnored private var keptStates: [String: AnyObject] = [:]

    private let tokens: any TokenStore
    private let urlSession: URLSession
    private let defaults: UserDefaults
    private let cloud: URL
    private let cloudWebApp: URL
    private let passkeyAuthorization = PasskeyAuthorization()
    private let browserSignIn = BrowserSignIn()
    private let openStore: (_ server: URL, _ userId: String) throws -> LocalStore
    private var refreshing: Task<Void, Never>?
    private var refreshWanted = (account: false, tracker: false)
    private var sessionWatch: Task<Void, Never>?
    private var polling: Task<Void, Never>?
    private var clock: Task<Void, Never>?
    private var trackerLoad: Task<Void, Never>?
    private var lastRefresh = Date.distantPast

    init(
        tokens: any TokenStore = Keychain(),
        urlSession: URLSession = AppModel.apiSession,
        defaults: UserDefaults = .standard,
        server: URL? = nil,
        cloud: URL = ServerAddress.cloud,
        formerClouds: [URL] = ServerAddress.formerClouds,
        cloudWebApp: URL = ServerAddress.cloudWebApp,
        openStore: @escaping (_ server: URL, _ userId: String) throws -> LocalStore = { try LocalStore(url: LocalStore.url(server: $0, userId: $1)) }
    ) {
        self.tokens = tokens
        self.urlSession = urlSession
        self.defaults = defaults
        self.cloud = cloud
        self.cloudWebApp = cloudWebApp
        self.openStore = openStore
        let chosen = defaults.string(forKey: Self.ownServerKey).flatMap { ServerAddress.address($0) }
        if server == nil && chosen == nil {
            Self.followCloud(to: cloud, formerly: formerClouds, tokens: tokens, defaults: defaults)
        }
        let server = server ?? chosen ?? cloud
        self.server = server
        client = Self.makeClient(server: server, tokens: tokens, urlSession: urlSession)
        section = defaults.string(forKey: "section").flatMap(AppSection.init(rawValue:)) ?? .timer
        startClock()
        connectivity.onOnline = { [weak self] in
            Task { await self?.catchUp() }
        }
        watchSession()
    }

    private static func makeClient(server: URL, tokens: any TokenStore, urlSession: URLSession) -> SpindleClient {
        SpindleClient(session: AuthSession(server: server, tokens: tokens, urlSession: urlSession), urlSession: urlSession)
    }

    /// Shows sign-in when the server ends the session.
    private func watchSession() {
        sessionWatch?.cancel()
        let session = client.session
        sessionWatch = Task { [weak self] in
            for await _ in session.sessionEnded {
                self?.sessionDidEnd()
            }
        }
    }

    /// API calls give up after 20 seconds, so a change waits in the outbox instead of the app hanging on a server
    /// that can't be reached.
    static let apiSession: URLSession = {
        let configuration = URLSessionConfiguration.default
        configuration.timeoutIntervalForRequest = 20
        return URLSession(configuration: configuration)
    }()

    /// Ticks the menu bar's clock. A timeline in the menu bar item itself would redraw it without end.
    private func startClock() {
        clock = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                let text = tracker?.running.map { Durations.clock($0.seconds(at: AppClock.now)) }
                if text != menuBarClock {
                    menuBarClock = text
                }
                // On the next whole second, so the clock steps evenly.
                let now = AppClock.now.timeIntervalSince1970
                try? await Task.sleep(for: .seconds(1 - (now - now.rounded(.down)) + 0.01))
            }
        }
    }

    // MARK: - Account

    /// The workspace the apps show: the one chosen last, on any device.
    var workspace: WorkspaceSummary? {
        guard let me else { return nil }
        return me.workspaces.first { $0.id == me.defaultWorkspaceId } ?? me.workspaces.first
    }

    /// Whether the person may create a workspace, as the server says. A server from before it said nothing let owners
    /// and admins of the current workspace's organization create them.
    var canCreateWorkspaces: Bool {
        guard let me, let workspace else { return false }
        return me.newWorkspace?.allowed ?? me.organizations.contains { $0.id == workspace.organizationId && $0.role != .member }
    }

    /// Says what a new workspace is and where it goes, before the person creates it.
    var newWorkspaceDescription: String {
        let space = "its own projects, clients, tags and team"
        let target = me?.newWorkspace
        let current = me?.organizations.first { $0.id == workspace?.organizationId }?.name
        if let target, target.allowed, target.organizationId == nil {
            let apart = current.map { "A space of your own, apart from \($0), with \(space)." } ?? "A space of your own, with \(space)."
            guard let days = target.trialDays else { return "\(apart) You own it and can invite people to it." }
            let plan = current.map { "It's not on \($0)'s plan: it" } ?? "It"
            return "\(apart) \(plan) starts its own \(days)-day free trial."
        }
        if let id = target?.organizationId, id != workspace?.organizationId,
           let elsewhere = me?.organizations.first(where: { $0.id == id })?.name {
            return "A separate space in \(elsewhere), with \(space). You become its admin."
        }
        return "A separate space with \(space). You become its admin."
    }

    var isWorkspaceAdmin: Bool {
        workspace?.role == .admin
    }

    /// The current workspace's organization, when its trial or plan has ended: its time can be read, reported and
    /// exported, and a running timer stopped, but nothing else changed.
    var readOnlyOrganization: OrganizationSummary? {
        guard let me, let workspace else { return nil }
        return me.organizations.first { $0.id == workspace.organizationId && $0.access == .readOnly }
    }

    /// The current workspace's organization, when the person may see its trial or plan: they own or administer it,
    /// on the hosted service. Self-hosted servers have no plans.
    var planOrganization: OrganizationSummary? {
        guard serverInfo?.hosted == true, let me, let workspace else { return nil }
        return me.organizations.first { $0.id == workspace.organizationId && $0.role != .member }
    }

    /// The web app, for what the Mac app leaves to it.
    func webURL(_ path: String) -> URL {
        (isCloud ? cloudWebApp : server).appending(path: path)
    }

    // MARK: - The server

    /// Whether the app talks to Spindle Cloud rather than someone's own server.
    var isCloud: Bool {
        server == cloud
    }

    /// Switches to Spindle Cloud or to the person's own server, while nobody is signed in. The choice is kept for the
    /// next launch.
    func useServer(_ url: URL) {
        guard phase == .signedOut, url != server else { return }
        server = url
        client = Self.makeClient(server: url, tokens: tokens, urlSession: urlSession)
        watchSession()
        signInNotice = nil
        // What the other server offers, e.g. its terms, is read again for the sign-in and sign-up forms.
        serverInfo = nil
        Task { await loadServerInfo() }
        if url == cloud {
            defaults.removeObject(forKey: Self.ownServerKey)
        } else {
            defaults.set(url.absoluteString, forKey: Self.ownServerKey)
        }
    }

    func useCloud() {
        useServer(cloud)
    }

    /// Whether a Spindle server answers at this address, before the app switches to it. Its health check says
    /// "Healthy" when the API runs and reaches its database.
    func isSpindleServer(_ url: URL) async -> Bool {
        guard let (data, response) = try? await urlSession.data(from: url.appending(path: "health")),
              (response as? HTTPURLResponse)?.statusCode == 200
        else { return false }
        return String(decoding: data, as: UTF8.self) == "Healthy"
    }

    /// When this build knows Spindle Cloud under a new address, e.g. after it moved to another domain, the sign-in and
    /// this Mac's copy of the account move along from the old one, so nobody is signed out and no waiting change is
    /// lost. The old address is the one the app saw last or one the build lists as former.
    ///
    /// Only real addresses count: debug builds share the release build's settings and Keychain, and their Spindle Cloud
    /// is the API on this Mac, which must not look like a move.
    static func followCloud(to cloud: URL, formerly former: [URL], tokens: any TokenStore, defaults: UserDefaults) {
        guard cloud.scheme == "https" else { return }
        let lastSeen = defaults.string(forKey: cloudKey).flatMap { URL(string: $0) }
        defaults.set(cloud.absoluteString, forKey: cloudKey)
        for previous in Set([lastSeen].compactMap { $0 } + former) where previous != cloud && previous.scheme == "https" {
            move(from: previous, to: cloud, tokens: tokens, defaults: defaults)
        }
    }

    /// Moves what this device keeps for one server to another, unless the other already has its own.
    private static func move(from previous: URL, to cloud: URL, tokens: any TokenStore, defaults: UserDefaults) {
        if let token = tokens.read(account: previous.absoluteString) {
            if tokens.read(account: cloud.absoluteString) == nil {
                tokens.write(token, account: cloud.absoluteString)
            }
            tokens.delete(account: previous.absoluteString)
        }
        guard let userId = defaults.string(forKey: lastUserKey(previous)) else { return }
        defaults.removeObject(forKey: lastUserKey(previous))
        guard defaults.string(forKey: lastUserKey(cloud)) == nil else { return }
        defaults.set(userId, forKey: lastUserKey(cloud))
        let from = LocalStore.url(server: previous, userId: userId).path(percentEncoded: false)
        let to = LocalStore.url(server: cloud, userId: userId).path(percentEncoded: false)
        for suffix in ["", "-wal", "-shm"] where !FileManager.default.fileExists(atPath: to + suffix) {
            try? FileManager.default.moveItem(atPath: from + suffix, toPath: to + suffix)
        }
    }

    /// The person's own server, when they chose one instead of Spindle Cloud.
    private static let ownServerKey = "ownServer"
    /// Spindle Cloud's address when the app last ran, to notice that it moved.
    private static let cloudKey = "cloudServer"

    /// Opens the session from the last launch, if there is one.
    func start() async {
        guard phase == .starting else { return }
        guard client.session.hasStoredSession else {
            phase = .signedOut
            return
        }
        // Open at once with what this device kept from last time, then catch up with the server.
        if let userId = defaults.string(forKey: Self.lastUserKey(server)) {
            await openAccount(userId: userId)
            if let cached = await account?.store.read(Me.self, key: "me") {
                me = cached
                phase = .signedIn
                startPolling()
                await loadAccount()
                return
            }
        }
        await loadAccount()
    }

    /// Loads the signed-in person, e.g. after signing in or to try again after a failure.
    func loadAccount() async {
        do {
            let me = try await client.me()
            await openAccount(userId: me.id)
            self.me = me
            await account?.store.write(me, key: "me")
            if phase != .signedIn {
                phase = .signedIn
                startPolling()
            }
        } catch let error as APIError where error.status == 401 {
            // Also right after signing in, when sign-in is still showing and would otherwise say nothing.
            showSignIn(notice: Self.sessionEndedNotice)
        } catch {
            // Opened from what this device kept, the app works on without the server.
            if phase != .signedIn {
                phase = .unavailable(error.localizedDescription)
            }
        }
    }

    /// A change refused as read-only means the organization became so since the person was loaded: load them again,
    /// so the app says so instead of failing change after change.
    private func learnReadOnly(from error: Error) {
        guard (error.asSpindleError as? APIError)?.isReadOnly == true, readOnlyOrganization == nil else { return }
        Task { try? await refreshAccount() }
    }

    /// Reloads the person and their workspaces in place, e.g. after a change on another device.
    func refreshAccount() async throws {
        let me = try await client.me()
        self.me = me
        await account?.store.write(me, key: "me")
    }

    // MARK: - Signing in and out

    func signIn(email: String, password: String) async throws {
        try await client.session.signIn(email: email, password: password)
        await didSignIn()
    }

    /// Native passkeys require one of the app's associated domains, which only Spindle Cloud is; localhost debug
    /// builds and people's own servers use passwords.
    var supportsPasskeys: Bool {
        isCloud && server.scheme == "https"
    }

    func signInWithPasskey() async throws {
        guard supportsPasskeys else { throw PasskeyError.unavailable }
        let challenge = try await client.session.passkeyOptions()
        let options = try PasskeyAssertionOptions(json: challenge.optionsJson)
        guard options.relyingParty == server.host() else { throw PasskeyError.invalidOptions }
        let credential = try await passkeyAuthorization.assertion(options)
        try await client.session.signInWithPasskey(credentialJSON: credential, challengeToken: challenge.challengeToken)
        await didSignIn()
    }

    /// Google, Apple or both, in the order the server lists them. None from a server that doesn't offer them, e.g. an
    /// older one or one run by a team without the providers set up.
    var signInProviders: [SignInProvider] {
        serverInfo?.signInProviders ?? []
    }

    /// Signs in with Google or Apple in a browser sheet; the server makes an account for someone new, who then lands
    /// where everyone who just signed up does. Closing the sheet or cancelling at the provider is no error.
    func signInExternally(with provider: String, invitation: String? = nil) async throws {
        let session = client.session
        let pkce = ExternalSignIn.PKCE()
        let start = ExternalSignIn.startURL(server: session.server, provider: provider, challenge: pkce.challenge, invitation: invitation)
        guard let callback = try await browserSignIn.open(start) else { return }
        switch ExternalSignIn.callback(callback) {
        case .code(let code):
            try await session.signInExternally(code: code, codeVerifier: pkce.verifier)
            // Another server may have been chosen meanwhile; the sign-in stays with the one it was made on.
            guard session === client.session else { return }
            await didSignIn()
        case .cancelled:
            return
        case .failed:
            throw ExternalSignInError.failed
        }
    }

    /// An account without a password confirms it's the person with a recent sign-in instead, which the server checks.
    func addPasskey(name: String, password: String?) async throws {
        guard supportsPasskeys else { throw PasskeyError.unavailable }
        let options = try await client.passkeyCreationOptions(password: password)
        guard options.relyingParty == server.host() else { throw PasskeyError.invalidOptions }
        let credential = try await passkeyAuthorization.registration(options)
        try await client.savePasskey(name: name, credentialJSON: credential)
    }

    /// Asks the server what it offers, for the sign-in and sign-up forms. Without an answer they offer everything,
    /// and the server says what it refuses.
    func loadServerInfo() async {
        let asked = server
        // An answer from the server chosen before is of no use once another one is.
        if let info = try? await client.session.serverInfo(), asked == server {
            serverInfo = info
        }
    }

    func register(fullName: String, email: String, password: String, acceptTerms: Bool) async throws {
        try await client.session.register(fullName: fullName, email: email, password: password, acceptTerms: acceptTerms)
        await didSignIn()
    }

    func requestPasswordReset(email: String) async throws {
        try await client.session.requestPasswordReset(email: email)
    }

    /// How many changes haven't reached the server yet.
    var waitingChanges: Int {
        account?.outbox.pending.count ?? 0
    }

    /// Signs out, first asking if changes haven't reached the server: signing out deletes them.
    func requestSignOut() {
        if waitingChanges > 0 {
            confirmingSignOut = true
        } else {
            Task { await signOut() }
        }
    }

    /// Signs out and deletes what this device kept for the account. `notice` says why, on the sign-in screen.
    func signOut(notice: String? = nil) async {
        showSignIn(notice: notice)
        closeAccount(deletingData: true)
        await client.session.signOut()
    }

    private func didSignIn() async {
        signInNotice = nil
        await loadAccount()
    }

    private static let sessionEndedNotice = "Your session has ended. Please sign in again."

    /// The server ended the session, e.g. it was signed out on another device or the password changed.
    private func sessionDidEnd() {
        guard phase != .signedOut else { return }
        showSignIn(notice: Self.sessionEndedNotice)
        // Changes that wait are kept: signing in again as the same person sends them.
        closeAccount(deletingData: false)
    }

    private func showSignIn(notice: String?) {
        me = nil
        keptStates = [:]
        signInNotice = notice
        editing = nil
        polling?.cancel()
        phase = .signedOut
    }

    // MARK: - Workspaces

    func switchWorkspace(to workspaceId: String) async throws {
        guard workspaceId != workspace?.id else { return }
        try await client.switchWorkspace(to: workspaceId)
        me?.defaultWorkspaceId = workspaceId
        try? await refreshAccount()
    }

    func createWorkspace(named name: String) async throws {
        let created = try await client.createWorkspace(name: name)
        me?.workspaces.append(created)
        me?.defaultWorkspaceId = created.id
        try? await refreshAccount()
    }

    // MARK: - Tracking

    /// Keeps the tracker on the current workspace, with the person's time zone and preferences.
    private func syncTracker() {
        guard let me, let workspace, let account else {
            trackerLoad?.cancel()
            tracker = nil
            return
        }
        let time = UserTime(me)
        if let tracker, tracker.workspace == workspace, tracker.time == time {
            return
        }
        // What the old tracker was still loading is of no use any more.
        trackerLoad?.cancel()
        let tracker = Tracker(api: client, outbox: account.outbox, store: account.store, workspace: workspace, time: time)
        self.tracker = tracker
        account.liveSync.watch(workspace.id)
        trackerLoad = Task {
            await tracker.restore()
            await tracker.load()
        }
    }

    // MARK: - This device's data for the account

    /// Opens the account's cache and outbox, and starts live sync.
    private func openAccount(userId: String) async {
        guard account?.userId != userId else { return }
        closeAccount(deletingData: false)
        let store = (try? openStore(server, userId)) ?? (try! LocalStore())
        let outbox = await Outbox(api: client, store: store)
        let liveSync = LiveSync(client: client, urlSession: urlSession)
        outbox.onSent = { [weak self] change, saved in
            self?.tracker?.didSend(change, saved)
        }
        outbox.onRejected = { [weak self] _, error, reported in
            self?.learnReadOnly(from: error)
            if !reported {
                let readOnly = (error.asSpindleError as? APIError)?.isReadOnly == true
                self?.notices.show(readOnly ? APIError.readOnlyMessage : "A change couldn't be saved: \(error.localizedDescription)", kind: .error)
            }
            self?.scheduleRefresh(tracker: true)
        }
        outbox.onConflict = { [weak self] _, current in
            // Shown under this device's change until the person chooses which to keep.
            self?.tracker?.didLearn(current)
        }
        liveSync.onChange = { [weak self] change in
            self?.handle(change)
        }
        liveSync.onReconnect = { [weak self] in
            // News may have been missed while the connection was down.
            Task { await self?.catchUp() }
        }
        account = Account(userId: userId, store: store, outbox: outbox, liveSync: liveSync)
        defaults.set(userId, forKey: Self.lastUserKey(server))
        liveSync.start()
        // Changes left from before go now, if the server can be reached.
        Task { await outbox.flush() }
    }

    private func closeAccount(deletingData: Bool) {
        guard let account else { return }
        account.liveSync.stop()
        self.account = nil
        tracker = nil
        keptStates = [:]
        if deletingData {
            let file = LocalStore.url(server: server, userId: account.userId)
            for suffix in ["", "-wal", "-shm"] {
                try? FileManager.default.removeItem(at: URL(filePath: file.path(percentEncoded: false) + suffix))
            }
            defaults.removeObject(forKey: Self.lastUserKey(server))
        }
    }

    private static func lastUserKey(_ server: URL) -> String {
        "account:\(server.absoluteString)"
    }

    /// Something changed on another device or for a teammate.
    private func handle(_ change: SyncChange) {
        switch change.topic {
        case "entries":
            // Teammates' time counts in reports; one's own also in the timer and calendar.
            if change.userId == me?.id {
                scheduleRefresh(tracker: true)
            } else {
                scheduleRefresh()
            }
        case "catalog":
            scheduleRefresh(tracker: true)
        case "billing", "team":
            // Billed time and members' rates count in reports.
            scheduleRefresh()
        case "settings":
            // The workspace's name shows in the switcher.
            scheduleRefresh(account: true, tracker: true)
        case "me":
            scheduleRefresh(account: true)
        case "agentActivity":
            agentActivityRevision += 1
        case "agentProposals":
            agentProposalsRevision += 1
        default:
            break
        }
    }

    /// Refreshes shortly, once for a burst of changes, e.g. several entries moved at once.
    private func scheduleRefresh(account: Bool = false, tracker: Bool = false) {
        refreshWanted.account = refreshWanted.account || account
        refreshWanted.tracker = refreshWanted.tracker || tracker
        refreshing?.cancel()
        refreshing = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(300))
            guard let self, !Task.isCancelled else { return }
            let wanted = refreshWanted
            refreshWanted = (false, false)
            dataRevision += 1
            if wanted.account {
                try? await refreshAccount()
            }
            if wanted.tracker {
                await self.tracker?.refresh()
            }
        }
    }

    /// Back in front after the system suspended the app, as on iPhone: live sync's connection is gone, so it connects
    /// again, sends what waited, and loads what changed meanwhile (at most every half minute).
    func becameActive() async {
        guard phase == .signedIn, let account else { return }
        account.liveSync.reconnectNow()
        await account.outbox.flush()
        await refreshAfterActivation()
    }

    /// Back online or reconnected: sends what waited and loads what was missed.
    private func catchUp() async {
        guard phase == .signedIn, let account else { return }
        await account.outbox.flush()
        account.liveSync.reconnectNow()
        await refresh()
    }

    /// Checks now and then for a timer started or stopped on another device.
    private func startPolling() {
        polling?.cancel()
        polling = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(60))
                // Live sync tells of changes as they happen; without it, the running timer is checked now and then.
                if self?.account?.liveSync.state != .connected {
                    await self?.tracker?.refreshRunning()
                }
            }
        }
    }

    /// Fetches everything again when the app comes to the front, at most every half minute.
    func refreshAfterActivation() async {
        guard phase == .signedIn, AppClock.now.timeIntervalSince(lastRefresh) > 30 else { return }
        lastRefresh = AppClock.now
        try? await refreshAccount()
        await tracker?.refresh()
    }

    func refresh() async {
        lastRefresh = AppClock.now
        dataRevision += 1
        try? await refreshAccount()
        await tracker?.refresh()
    }

    /// The state a screen keeps for the tracker's person and workspace, made on its first visit: going back to the
    /// screen shows what it showed at once, while it loads again behind it.
    func kept<State: AnyObject>(for tracker: Tracker, _ make: () -> State) -> State {
        let key = "\(State.self)|\(me?.id ?? "")|\(tracker.workspace.id)"
        if let state = keptStates[key] as? State { return state }
        let state = make()
        keptStates[key] = state
        return state
    }

    /// Puts the cursor in the timer's description field, to start typing what's next.
    func focusDescription() {
        section = .timer
        wantsDescriptionFocus = true
    }

    /// Starts a timer; says so and returns false if it couldn't.
    @discardableResult
    func start(_ details: EntryDetails = .empty) async -> Bool {
        guard let tracker else { return false }
        if readOnlyOrganization != nil {
            notices.show(APIError.readOnlyMessage, kind: .error)
            return false
        }
        do {
            try await tracker.start(details)
            return true
        } catch {
            learnReadOnly(from: error)
            notices.show(error, fallback: "Couldn't start the timer.")
            return false
        }
    }

    /// Stops the running timer, now or at an earlier time; when the workspace requires details it lacks, opens the
    /// entry to add them. Returns whether it stopped.
    @discardableResult
    func stop(at stop: Date = AppClock.now) async -> Bool {
        guard let tracker else { return false }
        switch await tracker.stopRunning(at: stop) {
        case .stopped:
            return true
        case .needsDetails(let entry):
            notices.show("Add the details this workspace requires to stop the timer.", kind: .error)
            editing = EntryEditing(entry: entry, stopping: true, stopAt: stop.timeIntervalSinceNow < -1 ? stop : nil)
            return false
        case .failed(let error):
            notices.show(error, fallback: "Couldn't stop the timer.")
            return false
        }
    }

    func toggleTimer() async {
        if tracker?.running != nil {
            await stop()
        } else {
            await start()
        }
    }

    /// Starts again with the most recent entry's details.
    func continueLastEntry() async {
        guard let tracker, let last = tracker.entries.filter({ !$0.isRunning }).max(by: { $0.start < $1.start }) else {
            notices.show("There is no entry to continue yet.", kind: .error)
            return
        }
        do {
            try await tracker.continueEntry(last)
            notices.show("Continuing “\(last.description.isEmpty ? "No description" : last.description)”")
        } catch {
            notices.show(error, fallback: "Couldn't start the timer.")
        }
    }
}

/// An entry to edit in a sheet, or the time for a new one.
struct EntryEditing: Identifiable {
    let id = UUID()
    var entry: TimeEntry?
    var newRange: DateInterval?
    /// The timer couldn't stop without details the workspace requires; saving stops it.
    var stopping = false
    /// When saving stops it, if not now, e.g. when the person left the Mac.
    var stopAt: Date?
    /// Parts to split the entry's time between, e.g. as agent activity suggested; saved only when the person saves.
    var split: [SplitPart]?
    /// Begins splitting the entry right away, by asking which project to split it with.
    var startSplit = false
    /// Consecutive stretches to save as separate entries, e.g. where agent activity moved to another project.
    var stretches: [TimeStretch]?
    /// What a new entry starts with, e.g. the details typed in the timer bar.
    var details: EntryDetails?
    /// Runs once the entry is saved, e.g. to clear the timer bar that offered it.
    var onSaved: (() -> Void)?
    /// Runs once the entry is saved as one entry, with its ID, e.g. to tell the server which entry a proposal became.
    var onSavedEntry: ((String) -> Void)?
    /// What an agent proposed for the entry, or for a new one: the editor opens with it, saved only when the person
    /// saves.
    var proposed: EntryRequest?
}

/// Part of an entry's time saved as an entry of its own, on one project.
struct TimeStretch: Hashable {
    var projectId: String
    var range: DateInterval
    /// The task a branch rule names for the stretch's project; the entry's own project keeps the entry's task.
    var taskId: String? = nil
}

/// What this device keeps for the signed-in account.
final class Account {
    let userId: String
    let store: LocalStore
    let outbox: Outbox
    let liveSync: LiveSync

    init(userId: String, store: LocalStore, outbox: Outbox, liveSync: LiveSync) {
        self.userId = userId
        self.store = store
        self.outbox = outbox
        self.liveSync = liveSync
    }
}
