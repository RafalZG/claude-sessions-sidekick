# Changelog

All notable changes to Claude Sessions Sidekick will be documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.4] — 2026-07-10

### Added
- Agents & Skills → Plugins & Skills: the Source dropdown now lists
  **User skills** (`~/.claude/skills`) alongside marketplace plugins and
  per-project skills. User-level skills are available in every project, so
  omitting them hid a whole scope. Shown even when the folder is empty
  (as `(0 skills)`) so its existence is discoverable. (Thanks Adrian.)

### Fixed
- Project names in the Session Browser and the widget header are now
  derived from each session's real working directory (`cwd`) instead of
  the lossy encoded folder key. In that key the drive colon, path
  separators, and literal `-`/`_` in folder names all collapse to `-`,
  so three distinct folders (`ExergyERP_Dev`, `ExergyERP-Dev`,
  `ExergyERP\Dev`) shared one project name and their sessions became
  indistinguishable — the "active session header shows a different
  session" report. Labels are now the shortest unique tail of the real
  path: non-colliding projects stay short (just the leaf folder), and
  only genuinely-colliding ones grow to `parent\leaf`, as far as needed.
- Session Browser Topic no longer reverts to Claude's auto-generated
  agent name after you `/rename` a session inside Claude Code. The
  session-names.json cache — populated from the ephemeral
  `~/.claude/sessions/*.json` PID files, which hold the *auto* name —
  was overwriting the `/rename` title parsed from the session log, so
  the CLI tab showed your chosen name (e.g. "Zarządzanie kontrahentami")
  while the browser still showed the old slug
  (e.g. `contractor-management-pos-filter`). A `/rename` is now
  authoritative and the auto-name cache can't clobber it. The
  Sidekick's own **Rename Topic** override still sits on top of both.

## [1.0.3] — 2026-06-22

### Changed
- Session Browser & widget Topic display now prefer the session's
  **slug** (e.g. `bad-debt-relief-screen`) over the raw first user
  message. The slug is what claude code itself shows in the CLI tab
  header on resume, so matching it makes cross-referencing a session
  in the list against the running tab obvious. Custom `/rename` titles
  still win over the slug, and the slug still falls back to the first
  user message when no slug has been generated yet (very fresh
  sessions). The Topic column tooltip still lists Name / FirstMessage /
  Slug / ID separately so the original context stays visible.

### Added
- Session Browser: per-session **Rename Topic** (right-click → Rename
  Topic...). The auto-derived topic chain — claude's /rename → first
  user message → kebab-case slug — sometimes lands on something
  unrecognizable when a session opens from a script or starts with a
  one-word prompt. The override displays in the Topic column and tooltip,
  survives claude rewriting the live session-name file, and clearing
  the box restores the original chain. Stored in
  `%APPDATA%\ClaudeSessionsSidekick\session-topics.json`, separate from
  `session-names.json` so claude's own /rename writes can't clobber it.
  Resolves [#1](https://github.com/RafalZG/claude-sessions-sidekick/issues/1).
- Session Browser context menu fully rebuilt from scratch with a single
  unified dark-theme template — items, separators, and submenu popups
  now render uniformly without the gutter strip, mismatched item heights,
  or two-tone separators that the iterative styling patches in 1.0.2
  kept producing. See `docs/MENU_STYLING_PLAN.md` for the spec.

### Fixed
- Resuming an Opus or Sonnet session no longer drops the 1M context window.
  `claude --resume <id>` without `--model` resolves the model from the
  session's JSONL snapshot ID, which routes to the 200k tier by default —
  so a session that was running as "Opus 4.8 (1M context)" would come
  back as plain "Opus 4.8" after Resume Session or Resume with effort.
  The Session Browser now derives the matching alias (`opus` / `sonnet`
  / `haiku`) from the session's recorded model family and passes it on
  every resume, keeping opus sessions opus, sonnet sessions sonnet, and
  haiku sessions haiku. Explicit per-session model picks via "Resume with
  model →" still override the auto-detected alias.

### Infrastructure
- Pinned `winget-releaser` to a `main`-branch SHA (2026-03-15) and passed
  the GitHub release tag URL via `release-notes-url`. The `v2` tag
  predates that input, so komac couldn't backfill `ReleaseNotes` /
  `ReleaseNotesUrl` and every release PR got blocked on wingetbot's
  `Manifest-Metadata-Consistency` check. v1.0.2 needed a manual
  locale.yaml fix on the PR branch; future releases should sail through.
- **Release build is now self-contained** (`--self-contained true`). The
  .NET 10 Desktop Runtime ships inside the installer, so the app installs
  and launches on a machine with no runtime present. This removes the
  `Microsoft.DotNet.DesktopRuntime.10` dependency that kept failing
  winget's sandbox install check ("No suitable installer found for
  Microsoft.DotNet.DesktopRuntime.10"). The installer grows from ~11 MB to
  ~60-70 MB, and the winget manifest no longer declares a runtime
  dependency. v1.0.2's winget submission is superseded by this release.

### Removed
- Debug → "Migrate data from earlier version..." helper. It existed only
  to one-click move user-data files (settings, favorites, notes, color
  tags, prompt library, session-browser layout) from the legacy
  `%APPDATA%\XGGClaudeUsageWidget` folder into the sidekick's data
  folder. The known users have migrated. Anyone who needs it later can
  still copy the JSON files by hand.

## [1.0.2] — 2026-06-16

### Fixed
- Tray widget no longer reports `~120% — Consider /compact now` on sessions
  that are well within their context window. `IsReducedContextModel` still
  flagged the bare `opus` and `sonnet` aliases as 200k-context (a holdover
  from the Claude 4.5 / 3.x era), so users with `"model": "opus"` in
  `settings.json` saw the widget claim 240k / 200k on what is actually a
  1M-token session — Opus 4.7+ / Sonnet 4.6+ aliases all resolve to 1M
  variants now. Only `haiku` still maps to a 200k model. Explicit legacy
  IDs (`claude-sonnet-4-5*`, `claude-3-*`) continue to match the
  reduced-context branch.

## [1.0.1] — 2026-06-16

### Added
- Session Browser: per-session model override via "Resume with model →" submenu
  (Sonnet / Opus / Haiku). Lets you park an older session on a newer model
  alias without having to `/model` after resume. Each pick is one-off; the
  next plain Resume Session goes back to whatever the session was using.
- Settings → General → "Resume effort": global default `--effort` (low / medium
  / high / xhigh / max) applied to every resume, plus a per-session
  "Resume with effort →" submenu in the Session Browser context menu.
  `ultracode` is intentionally not exposed — it auto-triggers dynamic-workflow
  orchestration on every substantive task and is too easy to leave on by
  accident. Power users can still set it inside the session via
  `/effort ultracode`.
- Session Browser: warn before resuming a session that already has a running
  `claude --resume {id}` process attached to it. Two concurrent claude windows
  on the same session JSONL diverge silently and can corrupt the file —
  detection is best-effort via a WMI command-line scan, and the dialog
  defaults to "Continue" so power users who genuinely want a second
  read-only view aren't blocked.

### Infrastructure
- Release workflow now auto-submits a manifest PR to `microsoft/winget-pkgs`
  via `vedantmgoyal2009/winget-releaser` on every clean `vMAJOR.MINOR.PATCH`
  tag (pre-release tags are skipped). Requires a `WINGET_TOKEN` repo secret
  with `public_repo` scope on the publishing account.

### Fixed
- Random key presses no longer fire global hotkeys when the OS reports a
  stuck modifier state. The hook used to trust `GetAsyncKeyState` alone,
  so a glitchy keyboard driver / RDP key injection / physical key that
  doesn't break clean would convince it that Win+Alt was held — every
  letter that matched a bound key would then trigger that shortcut. The
  hook now requires both tracked state (built from observed keydown/keyup
  events) and OS state to agree before firing, catching either failure
  mode. Also ignores injected (synthetic) key events so AutoHotkey-style
  tools don't drive our modifier tracking.
- Settings window no longer shows the "Shortcuts are currently disabled —
  enable via tray menu" warning on a fresh install. The detection check
  treated a missing registry value as "disabled", but `MainWindow.IsHotkeyEnabled`
  (the canonical source) treats it as "enabled (first run)" and actually
  registers the hotkeys on startup. Fresh users saw a scary yellow warning
  and were told to flip a tray-menu toggle that did nothing because hotkeys
  were already on.
- Disabling hotkeys via the tray menu now persists across app restarts.
  The previous implementation deleted the registry value on disable, which
  collapsed back into the "first run = enabled" branch on the next startup —
  hotkeys would silently re-register every time the user reopened the app.
  Disable now writes `0` explicitly so the three states (missing / 1 / 0)
  map cleanly to first-run / user-enabled / user-disabled.
- Resume now warns when the original project folder has been deleted instead of
  silently falling back to the user-profile directory (which produced a
  confusing "No conversation found" error from `claude --resume`). The new
  dialog offers to recreate the empty folder so the session can be opened in
  place.

## [1.0.0] — 2026-05-24

Stable release. No source changes since rc5 — this is a promotion of the
2026-05-18 build (commit `43a7e17`) to a clean version number after two
months of daily personal use across five release-candidate cycles.

The cumulative substance of the 1.0 line is documented in the
rc1 → rc5 entries below. Behaviour-wise, installing v1.0.0 is
indistinguishable from rc5; the bump is a stability and trust signal
(prominent in winget catalog and About window) rather than a feature
release. Auto-update from any earlier 1.0.0-rc tag picks this up via
the normal Velopack flow because semver ranks the final tag above any
prerelease (`1.0.0` > `1.0.0-rc5`).

Headline capabilities present at 1.0.0:
- Tray-resident usage widget (Mini / Compact / Full) with 5-hour
  rolling block and weekly Sonnet/Opus utilization
- Session browser across all projects with per-session notes, color
  tags, favorites, full-text JSONL search, per-cell token tooltips
- Project Quick Launchers with global low-level keyboard hotkeys,
  per-entry shell + model override
- Permission helper, Prompt library, Claude config + MCP browser,
  Memory + Agents/Skills viewers
- Auto-update via Velopack with opt-out + 8-hour throttle
- Active session staleness fixes (rc5) — no more false-positive
  "active" sessions after the user has finished

Known limitations carried into 1.0.0:
- Windows-only (WPF / .NET 10)
- Binary is unsigned. SignPath Foundation application declined
  2026-05-21 ("not yet enough public visibility"); re-application
  planned for ~August 2026. Recommended install paths (winget,
  Intune) bypass SmartScreen friction in the meantime.

## [1.0.0-rc5] — 2026-05-18

### Fixed
- Active session staleness: sessions closed 20-40 minutes ago no longer
  keep appearing as "active" in the widget. Root cause was in
  `SessionWatcherService.ProcessLine` — JSONL lines without a `timestamp`
  field (Claude Code occasionally emits `custom-title` / summary entries
  without one) fell back to `DateTimeOffset.UtcNow` for `LastSeen`. That
  fallback pumped `LastSeen` forward on every stray line, keeping closed
  sessions visible for the full 10-min `ActiveThreshold` after the user
  had actually finished. Timestamp-less lines now leave `LastSeen` alone.
- `TurnTimestamps` no longer pollutes its rate-of-activity history with
  the same fake "now" timestamp on user-typed lines that lack one. The
  turn count still increments (the message was real) — we just don't
  fabricate a time for it.
- Malformed `timestamp` values (free-form strings, partial ISO 8601, etc.)
  used to throw `FormatException` inside `DateTimeOffset.Parse`, which
  the outer try/catch swallowed — discarding the entire line including
  any `usage` token data on it. Switched to `TryParse`; bad timestamps
  now just skip the `LastSeen` update and let the rest of the line
  through normally.
- Tray widget header text "Claude Usage" → "Claude Sessions" (carryover
  from the XGGClaudeUsageWidget → claude-sessions-sidekick rename;
  every other window header had already been rebranded).

### Added
- Per-row tooltip on the widget's active-session list: hover any project
  name and you see "Last activity: X min ago". Surfaces the underlying
  recency so a user can spot a false-positive "active" sticking around
  if upstream JSONL parsing ever drifts again, and shows how close a
  session is to ageing out of the 10-min active window.

### Tests
- New `SessionWatcherServiceTests` (4 tests) covering the `LastSeen` fix,
  the timestamp-present positive control, the `TurnTimestamps` guard,
  and the malformed-timestamp tolerance. Suite now at 335 tests (was 331
  on rc4).

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

## [1.0.0-rc1] — 2026-05-10

First public release (originally documented under `[1.0.0]` here, but
the actual tag pushed at the time was `v1.0.0-rc1`; renamed in the
2026-05-24 stable-release prep to free up `[1.0.0]` for the actual
1.0.0 entry above).

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

[Unreleased]: https://github.com/RafalZG/claude-sessions-sidekick/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/RafalZG/claude-sessions-sidekick/releases/tag/v1.0.0
[1.0.0-rc5]: https://github.com/RafalZG/claude-sessions-sidekick/releases/tag/v1.0.0-rc5
[1.0.0-rc4]: https://github.com/RafalZG/claude-sessions-sidekick/releases/tag/v1.0.0-rc4
[1.0.0-rc3]: https://github.com/RafalZG/claude-sessions-sidekick/releases/tag/v1.0.0-rc3
[1.0.0-rc2]: https://github.com/RafalZG/claude-sessions-sidekick/releases/tag/v1.0.0-rc2
[1.0.0-rc1]: https://github.com/RafalZG/claude-sessions-sidekick/releases/tag/v1.0.0-rc1
