import AppKit
import SwiftUI
import SpindleKit

/// Connecting the person's agents to the server's MCP: Claude Code and Codex with their own commands, the address for
/// Claude and ChatGPT connectors, and the agents signed in.
struct AgentSettings: View {
    @Environment(Desktop.self) private var desktop
    @Environment(AppModel.self) private var model
    @State private var copied: String?
    @State private var agents: [DeviceSession] = []
    @State private var agentsError: String?

    var body: some View {
        let connector = desktop.connector
        Form {
            Section {
                ForEach(AgentConnector.Agent.allCases) { agent in
                    connectRow(agent, connector: connector)
                }
            } header: {
                Text("Connect an agent")
            } footer: {
                Text("Agents can read your time, describe entries, start and stop the timer, and propose time that waits for you to review. Each signs in through your browser, where you choose the workspace and whether it may change things.")
            }

            Section {
                LabeledContent {
                    copyButton(connector.url.absoluteString, label: "Copy Address")
                } label: {
                    Text(connector.url.absoluteString).textSelection(.enabled)
                }
            } header: {
                Text("Claude, ChatGPT and other apps")
            } footer: {
                Text("Add this address as a custom connector: in Claude under Settings › Connectors, in ChatGPT as a custom connector with developer mode on.")
            }

            Section {
                if agents.isEmpty {
                    Text(agentsError ?? "No agent has signed in yet.")
                        .foregroundStyle(agentsError == nil ? Color.secondary : Color.red)
                }
                ForEach(agents) { session in
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(session.deviceName ?? "Agent")
                            Text(details(session)).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("Sign Out") { signOut(session) }
                    }
                }
            } header: {
                Text("Signed-in agents")
            } footer: {
                Text("Signing an agent out stops it at once. They're also listed under Account, with your devices.")
            }

            Section {
            } footer: {
                Text("Agents see your agent activity only while “Sync agent activity to your account” is on in General: when you prompted agents, in which repository and on which branches. Prompts and commits never leave this Mac.")
            }
        }
        .formStyle(.grouped)
        .frame(height: 560)
        .task { await reload() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            Task { await reload() }
        }
    }

    private func connectRow(_ agent: AgentConnector.Agent, connector: AgentConnector) -> some View {
        let commands = agent.commands(connector.url)
        let connected = connector.connected.contains(agent)
        let working = connector.working?.agent == agent ? connector.working?.step : nil
        return VStack(alignment: .leading, spacing: 8) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(agent.label)
                    if connected {
                        Text("Added").font(.caption).foregroundStyle(.secondary)
                    }
                }
                Spacer()
                if working != nil {
                    Button("Cancel") { connector.cancel() }
                } else {
                    copyButton(commands, label: "Copy Commands")
                    Button(connected ? "Sign In Again" : "Connect") {
                        Task {
                            if connected { await connector.signInAgain(agent) } else { await connector.connect(agent) }
                            await reload()
                        }
                    }
                    .disabled(connector.working != nil)
                }
            }
            Text(commands)
                .font(.caption.monospaced())
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
            if let working {
                Text(working == .adding ? "Adding to \(agent.label)…" : "Finish signing in in your browser.")
                    .font(.caption)
            } else if let outcome = connector.outcome, outcome.agent == agent {
                Text(outcome.message)
                    .font(.caption)
                    .foregroundStyle(outcome.ok ? Color.secondary : Color.red)
                    .textSelection(.enabled)
            }
        }
        .padding(.vertical, 2)
    }

    private func copyButton(_ text: String, label: String) -> some View {
        Button(copied == text ? "Copied" : label) {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
            copied = text
        }
    }

    /// The workspace it reaches, whether it may change things, and when it was last used.
    private func details(_ session: DeviceSession) -> String {
        let access = session.agent.map { "\($0.workspaceName) · \($0.canWrite ? "Read and write" : "Read only")" }
        let used = "Last used \(session.lastActiveAt.formatted(.relative(presentation: .named)))"
        return [access, used].compactMap { $0 }.joined(separator: " · ")
    }

    private func reload() async {
        await desktop.connector.refresh()
        do {
            agents = try await model.client.sessions().filter { $0.agent != nil }.sorted { $0.lastActiveAt > $1.lastActiveAt }
            agentsError = nil
        } catch {
            agentsError = error.localizedDescription
        }
    }

    private func signOut(_ session: DeviceSession) {
        Task {
            do {
                try await model.client.revokeSession(session.id)
                await reload()
            } catch {
                agentsError = error.localizedDescription
            }
        }
    }
}
