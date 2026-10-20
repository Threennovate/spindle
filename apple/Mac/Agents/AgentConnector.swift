import Foundation
import Observation
import SpindleKit

/// Connects the person's agents, Claude Code and Codex, to the MCP server on the Spindle server the app is signed in
/// to (`<server>/mcp`), with the agents' own commands: it adds the server and starts signing in, which the person
/// finishes in their browser. The same commands are shown for copying, for when running them here doesn't work.
@Observable
final class AgentConnector {
    nonisolated enum Agent: String, CaseIterable, Identifiable {
        case claude, codex
        var id: Self { self }

        var label: String { self == .claude ? "Claude Code" : "Codex" }

        /// The command the agent is run with, to check it is installed.
        var program: String { rawValue }

        /// Adds the server for the user. Codex starts signing in on its own once it has added it.
        func add(_ url: URL) -> String {
            let name = AgentConnections.name
            return switch self {
            case .claude: "claude mcp add --transport http --scope user \(name) \(Self.quoted(url.absoluteString))"
            case .codex: "codex mcp add \(name) --url \(Self.quoted(url.absoluteString))"
            }
        }

        /// Takes out whatever the agent has by that name, so it can be added again.
        var remove: String {
            switch self {
            case .claude: "claude mcp remove --scope user \(AgentConnections.name)"
            case .codex: "codex mcp remove \(AgentConnections.name)"
            }
        }

        /// Signs in again: opens the browser and waits for it.
        var login: String { "\(program) mcp login \(AgentConnections.name)" }

        /// What the person would type in a terminal to connect it.
        func commands(_ url: URL) -> String {
            self == .claude ? "\(add(url))\n\(login)" : add(url)
        }

        /// In single quotes for the shell, which takes everything inside them as it is.
        static func quoted(_ text: String) -> String {
            text.allSatisfy { $0.isLetter || $0.isNumber || "-_./:@%+=,".contains($0) }
                ? text : "'" + text.replacingOccurrences(of: "'", with: "'\\''") + "'"
        }
    }

    enum Step: Equatable {
        case adding, signingIn
    }

    struct Outcome {
        let agent: Agent
        let ok: Bool
        let message: String
    }

    private let model: AppModel
    /// Which agents have the server at this app's address, as last read from their settings.
    private(set) var connected: Set<Agent> = []
    /// The agent being connected, and how far it got.
    private(set) var working: (agent: Agent, step: Step)?
    private(set) var outcome: Outcome?
    @ObservationIgnored private var running: ShellCommand?
    @ObservationIgnored private var cancelled = false

    init(model: AppModel) {
        self.model = model
    }

    /// The MCP server on the server the app is signed in to.
    var url: URL { AgentConnections.mcpURL(server: model.server) }

    /// Reads again which agents have the server, e.g. when Settings shows or after the person added it in Terminal.
    func refresh() async {
        let url = url
        connected = await Task.detached {
            let home = FileManager.default.homeDirectoryForCurrentUser
            var agents: Set<Agent> = []
            if AgentConnections.claude(home: home, url: url) { agents.insert(.claude) }
            if AgentConnections.codex(codexHome: AgentConnections.codexHome(home: home), url: url) { agents.insert(.codex) }
            return agents
        }.value
    }

    /// Adds the server to the agent, replacing anything it had by that name, and starts signing in. Says how it went.
    func connect(_ agent: Agent) async {
        await start(agent) {
            let url = self.url
            if agent == .claude {
                self.working = (agent, .adding)
                var added = await self.run(agent, agent.add(url))
                if !self.cancelled, added.output.localizedCaseInsensitiveContains("already exists") {
                    _ = await self.run(agent, agent.remove)
                    added = await self.run(agent, agent.add(url))
                }
                if let failure = self.failure(agent, added) { return failure }
                await self.refresh()
                return await self.signIn(agent)
            }
            // Codex replaces a server of the same name, and signs in straight after adding it.
            self.working = (agent, .signingIn)
            return self.signedIn(agent, await self.run(agent, agent.add(url), terminal: true, timeout: 600))
        }
    }

    /// Signs in to an agent that has the server already.
    func signInAgain(_ agent: Agent) async {
        await start(agent) { await self.signIn(agent) }
    }

    /// Stops waiting for the browser.
    func cancel() {
        cancelled = true
        running?.stop()
    }

    private func start(_ agent: Agent, _ work: () async -> Outcome) async {
        guard working == nil else { return }
        outcome = nil
        cancelled = false
        let result = await work()
        working = nil
        outcome = result
        await refresh()
    }

    private func signIn(_ agent: Agent) async -> Outcome {
        working = (agent, .signingIn)
        let result = await run(agent, agent.login, terminal: true, timeout: 600)
        if agent == .claude, result.output.localizedCaseInsensitiveContains("unknown command") {
            return Outcome(agent: agent, ok: true,
                           message: "Added to Claude Code. To sign in, run /mcp in Claude Code, choose \(AgentConnections.name), then Authenticate.")
        }
        return signedIn(agent, result)
    }

    private func signedIn(_ agent: Agent, _ result: ShellCommand.Result) -> Outcome {
        if result.timedOut {
            return Outcome(agent: agent, ok: false, message: "Not signed in. Connect again when you're ready, or sign in from \(agent.label).")
        }
        return failure(agent, result) ?? Outcome(agent: agent, ok: true, message: "Connected. Start a new session in \(agent.label) to use Spindle.")
    }

    /// How a command went wrong, if it did. A cancelled one, at any step, counts as not signed in, whatever it printed.
    private func failure(_ agent: Agent, _ result: ShellCommand.Result) -> Outcome? {
        if cancelled {
            return Outcome(agent: agent, ok: false, message: "Not signed in. Connect again when you're ready, or sign in from \(agent.label).")
        }
        if result.status == 0 { return nil }
        if result.timedOut {
            return Outcome(agent: agent, ok: false, message: "\(agent.label) didn't answer. Copy the commands instead.")
        }
        if result.status == 127 {
            return Outcome(agent: agent, ok: false,
                           message: "\(agent.label) isn't installed, or its command isn't on the shell's path. Copy the commands instead.")
        }
        let text = AgentConnections.plainText(result.output)
        return Outcome(agent: agent, ok: false, message: text.isEmpty ? "\(agent.label) didn't connect." : String(text.suffix(300)))
    }

    /// Runs one of the agent's commands in an interactive login shell, so it is found as in Terminal: installers put
    /// `claude` (`~/.local/bin`) and nvm's `codex` on the path in `.zshrc`, which a login shell alone doesn't read.
    /// Signing in runs in a pseudo-terminal: `claude mcp login` refuses to start without one. The command replaces the
    /// shell (`exec`), because an interactive shell ignores the SIGTERM that stopping it sends.
    private func run(_ agent: Agent, _ command: String, terminal: Bool = false, timeout: TimeInterval = 30) async -> ShellCommand.Result {
        let script = "command -v \(agent.program) >/dev/null 2>&1 || exit 127; exec " + (terminal ? "script -q /dev/null \(command)" : command)
        let shell = ShellCommand(script)
        running = shell
        defer { running = nil }
        if cancelled { return ShellCommand.Result(status: -1, output: "", timedOut: false) }
        return await shell.run(timeout: timeout)
    }
}

/// A command run by the person's shell, which can be stopped while it runs.
nonisolated final class ShellCommand: @unchecked Sendable {
    struct Result: Sendable {
        let status: Int32
        let output: String
        let timedOut: Bool
    }

    private let process = Process()
    private let lock = NSLock()
    private var timedOut = false
    private var printed = Data()

    init(_ script: String) {
        let environment = ProcessInfo.processInfo.environment
        process.executableURL = URL(fileURLWithPath: environment["SHELL"].flatMap { $0.isEmpty ? nil : $0 } ?? "/bin/zsh")
        process.arguments = ["-ilc", script]
        process.environment = environment.merging(["PATH": Self.searchPath(environment)]) { $1 }
        process.standardInput = FileHandle.nullDevice
    }

    func run(timeout: TimeInterval) async -> Result {
        await Task.detached { [self] in wait(timeout: timeout) }.value
    }

    private func wait(timeout: TimeInterval) -> Result {
        let output = Pipe()
        process.standardOutput = output
        process.standardError = output
        // Read as it comes rather than to the end: something the shell started may keep the pipe open after the shell
        // itself was killed.
        let ended = DispatchSemaphore(value: 0)
        output.fileHandleForReading.readabilityHandler = { [self] handle in
            let chunk = handle.availableData
            if chunk.isEmpty {
                handle.readabilityHandler = nil
                ended.signal()
            } else {
                lock.withLock { printed.append(chunk) }
            }
        }
        do { try process.run() } catch {
            output.fileHandleForReading.readabilityHandler = nil
            return Result(status: -1, output: error.localizedDescription, timedOut: false)
        }
        // A shell setup that waits for something, or a browser nobody finishes in, shouldn't keep it running.
        DispatchQueue.global().asyncAfter(deadline: .now() + timeout) { [self] in
            lock.withLock { timedOut = process.isRunning }
            stop()
        }
        process.waitUntilExit()
        // What it printed last, unless a child it left behind holds the pipe.
        _ = ended.wait(timeout: .now() + 1)
        output.fileHandleForReading.readabilityHandler = nil
        return lock.withLock {
            Result(status: process.terminationStatus, output: String(decoding: printed, as: UTF8.self), timedOut: timedOut)
        }
    }

    /// Ends it: SIGTERM, then SIGKILL if it is still running a moment later, since an interactive shell ignores
    /// SIGTERM, e.g. while its setup runs before it gets to `exec`. Under `script`, the agent's command goes with the
    /// pseudo-terminal either way.
    func stop() {
        guard process.isRunning else { return }
        process.terminate()
        DispatchQueue.global().asyncAfter(deadline: .now() + 2) { [process] in
            // Only while this process runs, so the identifier can't be another process's by now.
            if process.isRunning { kill(process.processIdentifier, SIGKILL) }
        }
    }

    /// The app's path, then where agents' installers put their commands, in case the shell's setup doesn't.
    private static func searchPath(_ environment: [String: String]) -> String {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let usual = ["\(home)/.local/bin", "\(home)/.claude/local", "/opt/homebrew/bin", "/usr/local/bin",
                     "\(home)/.bun/bin", "\(home)/.volta/bin", "\(home)/.npm-global/bin"]
        let current = (environment["PATH"] ?? "/usr/bin:/bin:/usr/sbin:/sbin").split(separator: ":").map(String.init)
        return (current + usual.filter { !current.contains($0) }).joined(separator: ":")
    }
}
