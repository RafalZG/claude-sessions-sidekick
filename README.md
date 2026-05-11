# Claude Sessions Sidekick

> Tray sidekick for [Claude Code](https://docs.claude.com/claude-code) — live usage tracking, session browser, quick launchers, and more.

A Windows tray widget that complements Claude Code with the things the CLI itself doesn't: a persistent at-a-glance view of your usage limits, a searchable browser for all your past sessions, per-session notes and color tags, project quick launchers with global hotkeys, and a prompt library.

**Status:** v1.0.0 — first public release. Tested on Windows 10/11 with .NET 10 runtime.

> **Note:** This is a hobby project built using [vibe coding](https://en.wikipedia.org/wiki/Vibe_coding) with Claude Code — most of the source is AI-assisted. It's been dogfooded by the author for a few months on real workloads, but please treat it as a useful utility rather than production-grade software. Bug reports liberally welcomed via [GitHub Issues](https://github.com/RafalZG/claude-sessions-sidekick/issues).

## What it does

### Live usage tracking
- 5-hour rolling block + weekly Sonnet/Opus utilization in your tray
- Optional `/compact` recommendations when context gets tight
- Auto-refresh on Claude Code OAuth token rotation
- Multiple view modes: Mini, Compact, Full

### Session browser
- All sessions across all projects in one list
- Full-text search inside session JSONL contents (filters out tool results, thinking signatures)
- Per-session free-text **notes** (right-click → Edit Note)
- Per-session **color tags** for visual grouping
- **Favorites** with greyed star ☆ for unflagged
- Persistent column widths/order + window size — right-click header to restore defaults
- Quick "Resume" launches `claude --resume {sessionId}` in your shell of choice

### Quick launchers
- Per-project entries with global hotkeys (low-level keyboard hook — works even when Claude Code isn't focused)
- Optional `--continue` (resume last session) per entry
- Per-entry shell override: CMD / PowerShell / Git Bash / Auto-detect
- Per-entry **model override** (Sonnet 1M / Opus 1M / Haiku 200k) — forces `--model X` on every launch, overrides whatever the project's last `/model` choice would otherwise produce

### Permission helper
- Watches Claude Code permission additions; suggests generalizing overly-narrow rules (e.g. `cd /specific/path && grep ...` → broader pattern) — opt-in

### Prompt library
- Stash reusable prompts; one-click "Send to Claude" (clipboard + launch)

### Claude config + MCP browser
- View detected Claude Code installation, model defaults, MCP servers per project
- Block/install MCP servers across projects

## Install

1. Download the latest `ClaudeSessionsSidekick.exe` from [Releases](https://github.com/RafalZG/claude-sessions-sidekick/releases)
2. Make sure you have [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed (Windows Desktop)
3. Double-click the exe; it lives in your tray
4. (Optional) right-click tray → Settings → Quick Launch to add your projects

The exe is currently **unsigned** — Cortex XDR / Defender SmartScreen may flag on first run. Code signing via SignPath OSS tier is in progress.

## Updates

The app checks for a new version on startup and shows a tray balloon when one is available. To install: right-click the tray icon → **Check for updates…**. The download + swap + restart is handled automatically (powered by [Velopack](https://github.com/velopack/velopack)).

## Build from source

Requires .NET 10 SDK + Windows.

```powershell
git clone https://github.com/RafalZG/claude-sessions-sidekick.git
cd claude-sessions-sidekick
dotnet build ClaudeSessionsSidekick.sln
dotnet test ClaudeSessionsSidekick.sln
dotnet run --project ClaudeSessionsSidekick.csproj
```

## Where data is stored

`%APPDATA%\ClaudeSessionsSidekick\`:
- `settings.json` — hotkeys, quick-launch entries, view preferences
- `favorites.json` — starred sessions
- `session-colors.json` — color tags
- `session-notes.json` — free-text notes per session
- `session-browser-layout.json` — column widths, window size
- `prompts.json` — prompt library
- `logs\` — recent app logs

## FAQ

**Q: Does this read my Claude Code conversations?**
Yes — it parses session JSONL files in `%USERPROFILE%\.claude\projects\` for the browser/search features. Everything stays on your machine; nothing is sent anywhere.

**Q: Why "Sidekick"?**
The tool is a sidekick *to* Claude Code, not a replacement — it adds tray-resident utilities and a session navigator that the CLI itself doesn't provide.

**Q: Will this work on macOS / Linux?**
No — WPF is Windows-only. A cross-platform port would need a rewrite.

**Q: Does this use Anthropic's API or my Claude Code subscription?**
The widget piggybacks on your local Claude Code installation. It reads the same OAuth token Claude Code uses for the usage limits API, and never makes any other API calls.

## Contributing

PRs welcome.

## Security & Privacy

Security issues: please report privately via [GitHub Security Advisories](https://github.com/RafalZG/claude-sessions-sidekick/security/advisories/new). See [SECURITY.md](SECURITY.md).

Privacy: the app does not transmit any data to the author or any third party. The only outbound network call is to Anthropic's official Claude Code usage API using your own OAuth token. See [PRIVACY.md](PRIVACY.md) for full details.

## License

[MIT](LICENSE) — © 2026 Rafał Zygmunt
