# scripts/

Maintenance and tooling scripts. Not built into the app; run manually from PowerShell.

---

## screenshot-mode.ps1

Replaces your real session data with sanitized demo sessions so you can take clean
README screenshots without leaking real prompts. Fully reversible.

### Usage

```powershell
# 1. Close the widget (tray icon -> Exit)
cd D:\path\to\claude-sessions-sidekick

# 2. Switch into screenshot mode
.\scripts\screenshot-mode.ps1 -Enable

# 3. Start the widget — it now sees only the 8 demo sessions
#    Take your screenshots (see "Recommended screenshot list" below)

# 4. Close the widget, switch back
.\scripts\screenshot-mode.ps1 -Disable

# 5. Restart the widget — your real data is back
```

### What gets touched

Everything is backed up before being replaced, with a `.screenshot-backup` suffix
next to the original. `-Disable` restores from those backups.

- `%USERPROFILE%\.claude\projects\` — moved to `projects.screenshot-backup`, replaced
  with 8 fake project folders + their JSONL sessions
- `%APPDATA%\ClaudeSessionsSidekick\` — these files get backed up + overlaid:
  - `favorites.json` — 2 fake favorites (demo-blog, api-gateway)
  - `session-colors.json` — 4 color tags (Blue / Green / Red / Yellow)
  - `session-notes.json` — 4 short notes referencing fake issues + versions
  - `session-names.json` — cleared (no custom-renamed sessions)
  - `settings.json` — 6 Quick Launch entries, Compact view mode, default hotkeys

### What's in the demo data

#### Sessions (visible in tray + Session Browser)

| # | Project | First prompt | Tag | Notes |
|---|---|---|---|---|
| 1 | demo-blog | fix react hydration mismatch on /posts route | ⭐ Blue | issue #234, **active** |
| 2 | api-gateway | refactor auth middleware to use jose v5 instead of golang-jwt | ⭐ | merge after security review, **active** |
| 3 | invoicing-app | add VAT calculation for B2B reverse charge customers | Green | shipped in v2.3.1, **borderline active** |
| 4 | dotfiles | migrate fish_config from 3.x to 4.0 syntax | — | small/quick |
| 5 | mobile-app | investigate flutter_secure_storage crash on android 14 | Red | high context %, large token use |
| 6 | personal-site | convert markdown blog to MDX with shiki syntax highlighting | — | medium |
| 7 | monorepo-tools | set up turborepo remote cache with vercel | Yellow | contains a `/compact` marker |
| 8 | db-migrations | write down-migration for 0042_add_user_settings | — | smallest |

#### Quick Launch entries (visible in Settings → Quick Launch)

| Name | Folder | Hotkey | Continue | Shell | Model |
|---|---|---|---|---|---|
| demo-blog | C:\Code\demo-blog | Win+Alt+1 | yes | PowerShell | — |
| api-gateway | C:\Code\api-gateway | Win+Alt+2 | — | Cmd | — |
| invoicing-app | C:\Code\invoicing-app | Win+Alt+3 | — | — | opus |
| mobile-app | C:\Code\mobile-app | Win+Alt+4 | — | — | — |
| dotfiles | C:\Users\dev\dotfiles | — | — | GitBash | haiku |
| monorepo-tools | C:\Code\monorepo-tools | Win+Alt+5 | yes | — | opus |

### Recommended screenshot list

For the README and the social preview, you probably want **5 shots**:

1. **Hero — tray expanded (Compact mode)**
   Open the tray icon, leave the widget at default Compact size. Capture the whole
   widget plus a small slice of the surrounding wallpaper so the floating-window
   look reads at a glance. This is the one image that "sells" the tool — make it
   the highest priority.

2. **Three-view comparison (Mini / Compact / Full)**
   Right-click tray → View Mode, cycle each. Capture three crops at the same
   scale, place side by side in your image editor. Optional but very effective.

3. **Session Browser**
   Open Session Browser (right-click tray → Sessions, or Win+LAlt+S). With the
   demo data, you'll see 8 sessions across projects with varied colors, stars,
   and "Last activity" values. Resize so 6-8 rows are visible. Hover the **In**
   column on the api-gateway row to surface the breakdown tooltip — that's
   worth capturing separately as a "we did the math right" detail shot.

4. **Quick Launch settings**
   Right-click tray → Settings → Quick Launch tab. The 6 entries above are
   visible with their hotkeys, shell overrides, and model overrides. Shows
   that the customization model is rich without making the screenshot dense.

5. **About window**
   Right-click tray → About. Small but it shows the version, links, and credit.
   Useful as a footer image.

#### Optional GIF

A 5-10 second screen recording of the tray icon click → widget expand → cycle
through view modes works very well as a hero asset. Tools: ScreenToGif, OBS.
Keep file size under 5 MB so GitHub renders inline.

### Where to put them

Commit the PNGs to `screenshots/` at the repo root, reference them from the main
README. For the GitHub social preview (Settings → General → Social preview),
crop one PNG to 1280×640.

### Troubleshooting

**"Widget is running (PID …)"**
Right-click the tray icon → Exit. The lock check is intentional — running
`-Enable` while the widget has JSONL files open corrupts the swap.

**"Backup folder already exists at …"**
A previous `-Enable` wasn't followed by `-Disable`. Investigate the backup
folder manually before resolving — your real data is there, intact. To
recover: rename `.screenshot-backup` folders back to the original names, then
re-run `-Enable` cleanly.

**Widget shows real data after `-Enable`**
You started it before the script finished, or you're hitting Sidekick's
6-hour scan window of stale state. Restart the widget after the script
prints "Screenshot mode ENABLED." — `FileSystemWatcher` picks up the demo
files on next startup.

**"Last activity" on demo-blog says 11+ min ago**
The active-session threshold is 10 minutes. If you take more than 7 minutes
between `-Enable` and the screenshot, the most recent demo session ages out
of "active". Re-run `-Disable` followed by `-Enable` to refresh the
timestamps, or edit `MinutesAgo` in the script for a longer freshness budget.

### Adding more demo content

Edit `screenshot-mode.ps1`:

- **Add another session** — append a hashtable to `$Sessions`. Set a stable
  `SessionId` GUID, a `Slug` that matches `{drive-letter}--{path-segments-with-hyphens}`,
  and `MinutesAgo` for the "Last activity" position. Prompts/Replies arrays
  must be the same length.
- **Add a Quick Launch entry** — append a hashtable to `$DemoSettings.quickLaunchEntries`
  with the same shape as the existing ones.
- **Star / color / note a new session** — add its `SessionId` to `$Favorites`,
  `$Colors`, or `$Notes` after the session definitions.

After any edit, validate the script parses by running:

```powershell
$errors = $null
[void][Management.Automation.Language.Parser]::ParseFile(
    "$PSScriptRoot\screenshot-mode.ps1", [ref]$null, [ref]$errors)
$errors
```

Empty output = clean parse.
