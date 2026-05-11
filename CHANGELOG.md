# Changelog

All notable changes to Claude Sessions Sidekick will be documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0-rc3] — 2026-05-11

Trust-protecting fix for the Session Browser's "Ctx %" column.

### Changed
- Session Browser: displayed Ctx % is now capped at "100%+" when the raw
  computed value exceeds the model's documented context window. The Anthropic
  API enforces prompt size ≤ context window, so any value above 100% means
  our accounting or the upstream usage report is off — showing a literal
  "306%" was misleading and undermined trust. The raw value is still used
  for column sorting and is logged once-per-session for diagnosis.

### Added
- Diagnostic log entry written to `app.log` the first time a session exceeds
  its model's context window by >5%. Captures model, configured-vs-shorthand
  flag, per-bucket token breakdown (input / cache read / cache creation),
  turn count, and source file. Latched per session so a long anomalous
  session doesn't flood the log.

## [1.0.0-rc2] — 2026-05-11

Polish + transparency pass on top of rc1.

### Changed
- About window: version display now uses Velopack-managed semver (so installed
  builds correctly show `1.0.0-rc2` instead of a stale `1.0.0` from the csproj
  default)
- About window: replaced the dead "Check for updates" link with a status hint
  pointing at the tray menu (the canonical place for updates)
- About window: redesigned Links section as a Grid for clean alignment and
  explicit `Segoe UI Emoji` so icons render in color instead of foreground-tinted
- README and About: explicit disclosure that the project is hobby vibe-coded with
  Claude Code; AI-assisted, not production-grade

### Added
- About window: new links to CHANGELOG and All releases on GitHub
- Release pipeline: `dotnet publish` now receives `-p:Version` matching the git
  tag, so the assembly's InformationalVersion stays in sync with the release

## [1.0.0] — 2026-05-10

First public release.

### Added
- Live tray view with 5-hour rolling block + weekly Sonnet/Opus utilization
- Three view modes: Mini, Compact, Full
- Session Browser: list/search/sort/filter all Claude Code sessions across projects
- Full-text search inside session JSONL contents (filters tool results, thinking signatures)
- Per-session free-text **notes** (right-click → Edit Note)
- Per-session **color tags**
- **Favorites** with star toggle
- Persistent column widths/order + window size; right-click header → Restore Default
- Quick "Resume" button → `claude --resume {sessionId}` in your shell of choice
- **Quick Launchers** with global low-level keyboard hotkeys (work even when Claude Code isn't focused)
- Per-launcher overrides: shell (CMD/PowerShell/Git Bash), `--continue`, **`--model X`** (sonnet/opus/haiku)
- Permission Watcher: opt-in suggestions to generalize narrow rules Claude Code adds
- Prompt Library: stash + one-click "Send to Claude"
- Claude config browser + MCP server install/block helpers
- Auto-update via [Velopack](https://github.com/velopack/velopack) — background check on startup, tray balloon notification, one-click install from tray menu

### Security
- Strict ASCII-only allow-list + 64-char cap on `--model` override values prevents shell injection from a hand-edited or malicious `settings.json`

### Known limitations
- Windows-only (WPF / .NET 10)
- Exe is unsigned — Cortex XDR / Defender SmartScreen may flag on first launch (SignPath OSS code-signing application in progress)
- The "Mini Buddy" mood indicator (in-tray emoji reflecting state) was started but is currently shelved; planned for a future release

[Unreleased]: https://github.com/RafalZG/claude-sessions-sidekick/compare/v1.0.0-rc3...HEAD
[1.0.0-rc3]: https://github.com/RafalZG/claude-sessions-sidekick/releases/tag/v1.0.0-rc3
[1.0.0-rc2]: https://github.com/RafalZG/claude-sessions-sidekick/releases/tag/v1.0.0-rc2
[1.0.0]: https://github.com/RafalZG/claude-sessions-sidekick/releases/tag/v1.0.0
