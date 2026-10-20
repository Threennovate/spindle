using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Spindle.Windows.Desktop;
using Spindle.Windows.Views.Shell;
using static Spindle.Windows.Views.SettingsView;

namespace Spindle.Windows.Views;

/// <summary>
/// Connecting the person's agents to the server's MCP: Claude Code and Codex with their own commands, the address for
/// Claude and ChatGPT connectors, and the agents signed in. The Mac's Agents tab.
/// </summary>
public sealed class AgentSettings : UserControl
{
    private readonly MainWindow owner;
    private readonly AgentConnector connector;
    private readonly StackPanel signedIn = Ui.Stack(0);

    public AgentSettings(MainWindow owner)
    {
        this.owner = owner;
        connector = owner.AgentConnector;
        Render();
        AttachedToVisualTree += async (_, _) =>
        {
            connector.Changed += Render;
            await connector.RefreshAsync();
            await LoadAgentsAsync();
        };
        DetachedFromVisualTree += (_, _) => connector.Changed -= Render;
    }

    private void Render()
    {
        // The signed-in agents load on their own, so their panel moves to each new rendering.
        (signedIn.Parent as Panel)?.Children.Remove(signedIn);
        if (connector.Url is not { } url)
        {
            Content = Footer("Sign in to connect agents.");
            return;
        }
        Content = Ui.Stack(0,
            Section("Connect an agent",
                Connect(AgentConnector.Agent.Claude, url),
                Connect(AgentConnector.Agent.Codex, url),
                Footer("Agents can read your time, describe entries, start and stop the timer, and propose time that waits for you to review. Each signs in through your browser, where you choose the workspace and whether it may change things.")),
            Section("Claude, ChatGPT and other apps",
                Card("IconLink", url.AbsoluteUri, null, Copy(url.AbsoluteUri, "Copy address", "Address copied.")),
                Footer("Add this address as a custom connector: in Claude under Settings › Connectors, in ChatGPT as a custom connector with developer mode on.")),
            Section("Signed-in agents", signedIn,
                Footer("Signing an agent out stops it at once. They're also listed under Account, with your devices.")),
            Section("Evidence",
                Footer("Agents see your agent activity only while “Sync agent activity to your account” is on in General: when you prompted agents, in which repository and on which branches. Prompts and commits never leave this PC.")));
    }

    private Control Connect(AgentConnector.Agent agent, Uri url)
    {
        var label = AgentConnector.Label(agent);
        var commands = AgentConnector.Commands(agent, url);
        var connected = connector.Connected.Contains(agent);
        var working = connector.Working is { } w && w.Agent == agent ? w.Step : (AgentConnector.Step?)null;
        Control action;
        if (working is not null)
        {
            action = Ui.Button("Cancel", connector.Cancel).Named("Cancel" + agent);
        }
        else
        {
            var connect = Ui.Button(connected ? "Sign in again" : "Connect", async () =>
            {
                if (connected) await connector.SignInAgainAsync(agent); else await connector.ConnectAsync(agent);
                await LoadAgentsAsync();
            }, owner.Notify, "primary").Named("Connect" + agent);
            connect.IsEnabled = connector.Working is null;
            action = Ui.Row(8, Copy(commands, "Copy commands", "Commands copied. Paste them in a terminal."), connect);
        }
        var text = new SelectableTextBlock { Text = commands, TextWrapping = TextWrapping.Wrap, FontSize = 12 }.Classed("mono");
        text.Res(SelectableTextBlock.ForegroundProperty, "TextSecondaryBrush");
        var status = working is { } step
            ? Ui.Wrapped(step == AgentConnector.Step.Adding ? $"Adding to {label}…" : "Finish signing in in your browser.", "caption")
            : connector.Last is { } last && last.Agent == agent ? Ui.Wrapped(last.Message, "caption", last.Ok ? "secondary" : "critical").Named("Outcome" + agent) : null;
        return Card("IconBot", label, connected ? "Added" : null, action, Ui.Stack(6, text, status));
    }

    private Button Copy(string text, string label, string notice) => Ui.Button(label, async () =>
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        owner.Notify(notice, NoticeKind.Success);
    }, owner.Notify);

    private async Task LoadAgentsAsync()
    {
        if (owner.Api is not { } api) return;
        try
        {
            var agents = (await api.GetAsync<AgentSession[]>("/api/me/sessions")).Where(s => s.Agent is not null).OrderByDescending(s => s.LastActiveAt).ToList();
            signedIn.Children.Clear();
            if (agents.Count == 0) signedIn.Children.Add(Card(null, "No agent has signed in yet.", null, null));
            foreach (var session in agents)
            {
                var access = $"{session.Agent!.WorkspaceName} · {(session.Agent.CanWrite ? "Read and write" : "Read only")} · Last used {AccountSettings.Relative(session.LastActiveAt)}";
                signedIn.Children.Add(Card("IconBot", session.DeviceName ?? "Agent", access, Ui.Button("Sign out", async () =>
                {
                    await api.DeleteAsync("/api/me/sessions/" + session.Id);
                    await LoadAgentsAsync();
                }, owner.Notify)));
            }
        }
        catch (Exception)
        {
            signedIn.Children.Clear();
            signedIn.Children.Add(Footer("Couldn't load your agents."));
        }
    }

    public sealed record AgentSession
    {
        public Guid Id { get; init; }
        public string? DeviceName { get; init; }
        public DateTimeOffset LastActiveAt { get; init; }
        public AgentAccess? Agent { get; init; }
    }

    public sealed record AgentAccess
    {
        public string WorkspaceName { get; init; } = "";
        public bool CanWrite { get; init; }
    }
}
