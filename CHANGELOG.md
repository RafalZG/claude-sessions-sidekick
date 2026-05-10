# Changelog

All notable changes to Claude Sessions Sidekick will be documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

### Security
- Strict ASCII-only allow-list + 64-char cap on `--model` override values prevents shell injection from a hand-edited or malicious `settings.json`

### Known limitations
- Windows-only (WPF / .NET 10)
- Exe is unsigned — Cortex XDR / Defender SmartScreen may flag on first launch (signing planned for a post-1.0 release)
- The "Mini Buddy" mood indicator (in-tray emoji reflecting state) was started but is currently shelved; planned for a future release

[Unreleased]: https://github.com/RafalZG/claude-sessions-sidekick/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/RafalZG/claude-sessions-sidekick/releases/tag/v1.0.0
