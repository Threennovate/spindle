# Connecting your agents

Spindle has an MCP server, so Claude Code, Codex, Claude (claude.ai, the desktop and phone apps) and other MCP clients
can read your time and, if you let them, describe entries, run the timer and propose time for you to confirm. It is at
`/mcp` on your Spindle address: `https://app.spindletrack.com/mcp` on Spindle Cloud, or e.g.
`https://spindle.example.com/mcp` on your own server.

Each client signs in through your browser: Spindle shows which agent asks, you choose the workspace it may reach and
whether it may change things, and it is then listed under **Account › Where you're signed in**, where you can sign it
out. Agents never change times, projects or splits themselves; they propose them, and the proposals wait in Spindle
until you add, edit or discard them.

The Mac and Windows apps do the setup for you: **Settings › Agents › Connect** next to Claude Code or Codex runs the
commands below for the server you're signed in to, replacing anything those agents had under the name `spindle`, and
opens the browser to sign in. Settings also shows the commands to copy, the address for Claude and ChatGPT, and the
agents signed in.

## Claude Code

```bash
claude mcp add --transport http --scope user spindle https://app.spindletrack.com/mcp
```

Then sign in with `/mcp` in Claude Code (choose `spindle`, then Authenticate), or from a terminal:

```bash
claude mcp login spindle
```

If Claude Code already has a server called `spindle`, remove it first with
`claude mcp remove --scope user spindle`, or give this one another name.

## Codex

```bash
codex mcp add spindle --url https://app.spindletrack.com/mcp
```

Codex opens the browser to sign in straight away; `codex mcp login spindle` does it again later. Or add it to
`~/.codex/config.toml` yourself and then run `codex mcp login spindle`:

```toml
[mcp_servers.spindle]
url = "https://app.spindletrack.com/mcp"
```

## Claude on the web, desktop and phone

In Claude, open **Settings › Connectors**, choose **Add custom connector**, name it Spindle and give it
`https://app.spindletrack.com/mcp`. Claude opens Spindle to sign in. Once added, the connector also works in the
Claude apps on the desktop and the phone.

## ChatGPT

Turn on developer mode in ChatGPT's settings (on a workspace, an admin may have to allow custom connectors; which
plans have it, and where the menus are, has changed over time), then create a connector named Spindle with
`https://app.spindletrack.com/mcp` and OAuth. ChatGPT opens Spindle to sign in.

## What agents see

- Your entries, projects and reports in the workspace you chose, and the proposals waiting for you.
- Agent activity only if you turned on **Account › Agent activity › Sync agent activity from your computers**: when you
  prompted agents, in which repository and on which branches, and session titles if you sync those too. Prompts and
  commits never reach Spindle. The evidence names the repository's git origin and the time window, so an agent that
  has the repository at hand reads the commits itself.

Allowed only to read, an agent can't change anything. Allowed to write, it can describe entries (only empty ones unless
you ask), start and stop the timer, and propose entries and changes.
