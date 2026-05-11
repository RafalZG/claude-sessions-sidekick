# Changelog

All notable changes to Claude Sessions Sidekick will be documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0-rc4] — 2026-05-11

### Fixed
- About window: link icons are now actually visible on the dark background.
  Previous rc2/rc3 attempt set `FontFamily="Segoe UI Emoji"` but WPF's text
  pipeline doesn't render colour emoji glyphs (no DirectWrite COLR/CPAL
  support) — every glyph was being drawn as a monochrome tint in the
  inherited `Foreground`, which defaulted to black. Replaced the emoji
  codepoints with monochrome Unicode symbols (★ ⚠ ✎ ◉) and set explicit
  bright `Foreground` per icon, matching the window's accent colors.
- Stale "update available" toasts in Windows Action Center are now cleared
  on app startup (`ToastNotificationManager.History.Clear()`), so a user
  who already installed the update doesn't keep seeing a notification
  about the version they're already running.

### Added
- Setting "Check for updates on startup" (Settings → General → Updates).
  Default on. When off, no automatic check runs — the user still has the
  manual "Check for updates…" tray menu action.
- 8-hour throttle on the automatic update check. If several releases ship
  on the same day, a user who restarts the app multiple times in that
  window only gets one toast notification instead of one per restart.
  Manual checks via the tray menu bypass the throttle.

### Changed
- Session Browser "In" column now has a per-cell tooltip showing the
  breakdown into fresh input, cache reads, and cache writes — plus the
  percentage of total taken by cache reads. The headline "1.8B tokens"
  number was scaring users; the tooltip makes clear that ~95% of it is
  cache reads, billed at roughly 1/10 the rate of fresh input. The cell
  value itself is unchanged (still the lump sum), so column sorting
  works the same way.
- "Turns" column now counts real user-typed prompts instead of assistant
  messages. Previous logic incremented on every assistant message that
  contained `text` or `thinking` content — but those are emitted on every
  tool round, not once per user prompt. A heavy session with 200 user
  prompts and ~10 tool rounds each was reporting "2000 turns". The new
  count matches what users intuitively call a "turn".
- Target framework bumped from `net10.0-windows` to
  `net10.0-windows10.0.19041.0` to access WinRT
  `Windows.UI.Notifications.ToastNotificationManager`. Minimum supported
  Windows version is now Win10 May 2020 Update (build 19041); older
  builds are end-of-life and not supported anyway.

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

[Unreleased]: https://github.com/RafalZG/claude-sessions-sidekick/compare/v1.0.0-rc4...HEAD
[1.0.0-rc4]: https://github.com/RafalZG/claude-sessions-sidekick/releases/tag/v1.0.0-rc4
[1.0.0-rc3]: https://github.com/RafalZG/claude-sessions-sidekick/releases/tag/v1.0.0-rc3
[1.0.0-rc2]: https://github.com/RafalZG/claude-sessions-sidekick/releases/tag/v1.0.0-rc2
[1.0.0]: https://github.com/RafalZG/claude-sessions-sidekick/releases/tag/v1.0.0
