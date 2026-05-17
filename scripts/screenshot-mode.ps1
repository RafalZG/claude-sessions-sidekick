<#
.SYNOPSIS
    Switches Claude Sessions Sidekick into screenshot mode by replacing
    your real session data with sanitized demo sessions.

.DESCRIPTION
    -Enable backs up your real %USERPROFILE%\.claude\projects folder and the
    Sidekick overlay files (favorites / colors / notes) under %APPDATA%,
    then drops in 8 hand-crafted demo sessions plus matching overlays so
    you can take clean README screenshots without leaking real prompts.

    -Disable reverses everything. The backups live next to the real
    folders with a .screenshot-backup suffix, so a failed/interrupted run
    is recoverable manually.

    The widget must be closed before running either mode — JSONL file
    locking otherwise interferes with the swap.

.PARAMETER Enable
    Enter screenshot mode.

.PARAMETER Disable
    Leave screenshot mode and restore the real data.

.EXAMPLE
    .\screenshot-mode.ps1 -Enable
    # close + restart the widget, take screenshots
    .\screenshot-mode.ps1 -Disable
#>

[CmdletBinding(DefaultParameterSetName = 'Help')]
param(
    [Parameter(ParameterSetName = 'Enable', Mandatory)]
    [switch]$Enable,

    [Parameter(ParameterSetName = 'Disable', Mandatory)]
    [switch]$Disable
)

$ErrorActionPreference = 'Stop'

$ProjectsDir    = Join-Path $env:USERPROFILE '.claude\projects'
$ProjectsBackup = "$ProjectsDir.screenshot-backup"
$AppData        = Join-Path $env:APPDATA 'ClaudeSessionsSidekick'
$OverlayFiles   = @('favorites.json', 'session-colors.json', 'session-notes.json', 'session-names.json', 'settings.json')

function Assert-WidgetNotRunning {
    $proc = Get-Process -Name 'ClaudeSessionsSidekick' -ErrorAction SilentlyContinue
    if ($proc) {
        throw "Widget is running (PID $($proc.Id)). Right-click tray icon -> Exit, then re-run."
    }
}

# 8 hand-crafted demo sessions. Stable GUIDs so favorites/colors/notes
# overlay correctly across re-runs of -Enable.
$Sessions = @(
    @{
        SessionId  = '8a4c1f2d-1111-4aaa-9000-000000000001'
        Slug       = 'C--Code-demo-blog'
        Cwd        = 'C:\Code\demo-blog'
        Branch     = 'main'
        Model      = 'claude-opus-4-7'
        MinutesAgo = 3
        Usage      = 'medium'
        Compact    = $false
        Prompts = @(
            'fix react hydration mismatch on /posts route'
            'show me the diff'
            'apply it and run the build'
            'ship it'
        )
        Replies = @(
            'Looking at app/posts/page.tsx — I see a Date.now() call inside the server component that won''t match on hydration. Switching to a stable build-time timestamp.'
            'Replaced Date.now() with process.env.BUILD_TIMESTAMP and threaded it through the layout. Diff is in the file.'
            'Build passed in 14s, no hydration warnings in dev.'
            'Done — pushed to main.'
        )
    }
    @{
        SessionId  = '8a4c1f2d-2222-4aaa-9000-000000000002'
        Slug       = 'C--Code-api-gateway'
        Cwd        = 'C:\Code\api-gateway'
        Branch     = 'feat/jose-v5'
        Model      = 'claude-opus-4-7'
        MinutesAgo = 7
        Usage      = 'large'
        Compact    = $false
        Prompts = @(
            'refactor auth middleware to use jose v5 instead of golang-jwt'
            'jose v5 doesn''t have ES256 by default?'
            'add the import for the algorithm'
            'run the tests'
            'fix the one that failed'
            'verify nothing else regressed'
            'write a CHANGELOG entry'
        )
        Replies = @(
            'middleware/auth.go uses golang-jwt with HS256. I''ll rewrite using github.com/go-jose/go-jose/v4 (jose v5 ships under the v4 module path in Go).'
            'Right — you need to register the algorithm explicitly via the JOSE header rather than the legacy SigningMethodES256 helper.'
            'Added the import to handler.go.'
            'Tests: 47 passed, 1 failed — TestAuthMiddleware_ExpiredToken expected a specific error string.'
            'Updated the assertion to match the jose error wrapper. All 48 green.'
            'Ran the full suite including integration tests — no regressions.'
            'Added entry to CHANGELOG.md under Unreleased > Changed.'
        )
    }
    @{
        SessionId  = '8a4c1f2d-3333-4aaa-9000-000000000003'
        Slug       = 'C--Code-invoicing-app'
        Cwd        = 'C:\Code\invoicing-app'
        Branch     = 'main'
        Model      = 'claude-sonnet-4-6'
        MinutesAgo = 9
        Usage      = 'medium'
        Compact    = $false
        Prompts = @(
            'add VAT calculation for B2B reverse charge customers'
            'where should the toggle live — customer entity or invoice line?'
            'go with customer entity'
            'add unit tests'
            'review the migration'
        )
        Replies = @(
            'B2B reverse charge: supplier issues a 0% VAT invoice, buyer self-accounts. I''ll add an IsReverseChargeApplicable flag.'
            'Customer entity — it''s a relationship attribute (EU B2B with valid VAT-ID), not a per-line concern. On invoice line it would duplicate logic.'
            'Adding to Customer.cs and threading through InvoiceService.CalculateVat.'
            '6 new tests covering domestic B2C, domestic B2B, EU B2B reverse charge, non-EU export, mixed-line invoice, and the override toggle.'
            'Migration 0078_add_reverse_charge_flag.sql looks clean — nullable bit column with default 0, backfilled for existing EU customers with VAT-IDs.'
        )
    }
    @{
        SessionId  = '8a4c1f2d-4444-4aaa-9000-000000000004'
        Slug       = 'C--Users-dev-dotfiles'
        Cwd        = 'C:\Users\dev\dotfiles'
        Branch     = 'main'
        Model      = 'claude-haiku-4-5'
        MinutesAgo = 60
        Usage      = 'small'
        Compact    = $false
        Prompts = @(
            'migrate fish_config from 3.x to 4.0 syntax'
            'show me the breaking changes you found'
        )
        Replies = @(
            'fish 4 deprecates the implicit "set -e x; set x v" idiom in favor of "set --erase x; set x v", and the --on-event handlers shifted slightly. Updated config.fish.'
            'Three places: prompt event handler (now --on-variable), the abbr command (abbr -a not abbreviation), and the function autoloading path.'
        )
    }
    @{
        SessionId  = '8a4c1f2d-5555-4aaa-9000-000000000005'
        Slug       = 'C--Code-mobile-app'
        Cwd        = 'C:\Code\mobile-app'
        Branch     = 'bug/secure-storage-crash'
        Model      = 'claude-opus-4-7'
        MinutesAgo = 2880  # 2 days
        Usage      = 'huge'
        Compact    = $false
        Prompts = @(
            'investigate flutter_secure_storage crash on android 14'
            'pull the stack trace from the analytics dashboard'
            'is this the issue from github 579?'
            'try the workaround'
            'still crashing — check if it''s a keystore issue'
            'find the keystore alias generation code'
            'why does it use sha-1?'
            'switch to sha-256'
            'rebuild and ship to internal track'
            'works on pixel 8 — get a tester report'
            'great — close the bug'
            'write a postmortem'
        )
        Replies = @(
            'flutter_secure_storage 8.x has a known incompat with Android 14''s strict-mode keystore changes. Let me check the crash signature.'
            'Stack trace shows ProviderException at AesGcm.encrypt() — keystore-related.'
            'Yes — issue mogol/flutter_secure_storage#579. Workaround is AndroidOptions(encryptedSharedPreferences: true).'
            'No change. The encrypted preferences fallback uses the same keystore alias.'
            'Right — Android 14 stops respecting SHA-1-derived aliases for new entries.'
            'Found it: SecureStorageImpl.kt line 87 uses MessageDigest.getInstance("SHA-1").'
            'Cargo-cult from an older sample. SHA-1 has been deprecated for keystore aliasing since Android 12.'
            'Switched to SHA-256, regenerated the alias, added a one-shot migration that re-encrypts existing data under the new alias.'
            'Built — internal track 1.18.0+47, pushed to Play Console.'
            'Pixel 8 / Android 14 tester reports no crashes after 30 min of normal usage. Old data migrated successfully.'
            'Closed #579 internally with notes referencing the upstream issue.'
            'Wrote docs/postmortems/2026-05-mobile-keystore-crash.md — root cause, timeline, fix, prevention notes.'
        )
    }
    @{
        SessionId  = '8a4c1f2d-6666-4aaa-9000-000000000006'
        Slug       = 'C--Code-personal-site'
        Cwd        = 'C:\Code\personal-site'
        Branch     = 'mdx-migration'
        Model      = 'claude-sonnet-4-6'
        MinutesAgo = 5760  # 4 days
        Usage      = 'medium'
        Compact    = $false
        Prompts = @(
            'convert markdown blog to MDX with shiki syntax highlighting'
            'rename the .md files or keep both extensions?'
            'rename all'
            'set up shiki with the github-dark theme'
            'preview a few posts'
            'looks good, deploy it'
        )
        Replies = @(
            'Astro 4 has first-class MDX via @astrojs/mdx. I''ll install the integration and the rehype-shiki plugin.'
            'Rename — mixed extensions just confuse the routing.'
            '42 files renamed in src/content/blog/. Frontmatter parsed cleanly, no broken slugs.'
            'Configured shiki with github-dark via rehype-pretty-code. Added inline-code styling too.'
            'Preview server up at localhost:4321 — code blocks have proper highlighting plus copy buttons.'
            'Built (0 warnings, 0.8s) and deployed to Cloudflare Pages.'
        )
    }
    @{
        SessionId  = '8a4c1f2d-7777-4aaa-9000-000000000007'
        Slug       = 'C--Code-monorepo-tools'
        Cwd        = 'C:\Code\monorepo-tools'
        Branch     = 'main'
        Model      = 'claude-opus-4-7'
        MinutesAgo = 10080  # 1 week
        Usage      = 'large'
        Compact    = $true
        Prompts = @(
            'set up turborepo remote cache with vercel'
            'is the team plan enough or do we need enterprise?'
            'follow the team plan path'
            'authenticate the CI runner'
            'add the env var to github actions'
            'verify cache hits'
            'document the setup for the team'
            'add a runbook for cache invalidation'
        )
        Replies = @(
            'Turborepo 2.x has built-in vercel remote cache via "turbo login" + "turbo link". Team plan covers it.'
            'Team plan — enterprise adds SSO and SLAs we don''t need at our size.'
            'turbo link ran, linked to the workspace.'
            'CI runner needs TURBO_TOKEN + TURBO_TEAM env vars. Generated a CI-scoped token from the dashboard.'
            'Added both to repo secrets and wired into the workflow envs.'
            'First CI run: full build, all tasks cached on vercel side. Second run: 8/10 tasks restored from remote, 12s total vs 4m20s without.'
            'Wrote docs/setup/turborepo-remote-cache.md — what it does, how to opt out locally, troubleshooting.'
            'Wrote docs/runbooks/turbo-cache-invalidation.md — turbo prune-cache --filter and the dashboard nuke button.'
        )
    }
    @{
        SessionId  = '8a4c1f2d-8888-4aaa-9000-000000000008'
        Slug       = 'C--Code-db-migrations'
        Cwd        = 'C:\Code\db-migrations'
        Branch     = 'main'
        Model      = 'claude-sonnet-4-6'
        MinutesAgo = 30240  # 3 weeks
        Usage      = 'small'
        Compact    = $false
        Prompts = @(
            'write down-migration for 0042_add_user_settings'
            'what about the rows — drop or preserve?'
            'drop is fine, this is staging only'
        )
        Replies = @(
            'Looking at 0042_add_user_settings.up.sql — it creates the user_settings table with a FK to users. The reverse is DROP TABLE IF EXISTS, since the FK lives on the new table.'
            'Two options: drop (data gone) or move to user_settings_archive. Up-migration didn''t backfill anything semantically meaningful.'
            'Writing 0042_add_user_settings.down.sql with DROP TABLE IF EXISTS user_settings;'
        )
    }
)

# Approximate token sizes per turn for each usage profile.
$UsagePresets = @{
    small  = @{ Input =  80; Out =  200; CacheRead =   2500; CacheCreate =  1200 }
    medium = @{ Input = 200; Out =  500; CacheRead =   8000; CacheCreate =  3500 }
    large  = @{ Input = 350; Out =  900; CacheRead =  22000; CacheCreate =  8000 }
    huge   = @{ Input = 600; Out = 1400; CacheRead = 180000; CacheCreate = 15000 }
}

# Overlay assignments: which demo sessions get a star, color tag, note.
$Favorites = @($Sessions[0].SessionId, $Sessions[1].SessionId)

$Colors = [ordered]@{}
$Colors[$Sessions[0].SessionId] = 'Blue'
$Colors[$Sessions[2].SessionId] = 'Green'
$Colors[$Sessions[4].SessionId] = 'Red'
$Colors[$Sessions[6].SessionId] = 'Yellow'

$Notes = [ordered]@{}
$Notes[$Sessions[0].SessionId] = 'issue #234 — also touching middleware.ts'
$Notes[$Sessions[1].SessionId] = 'merge after security review'
$Notes[$Sessions[2].SessionId] = 'shipped in v2.3.1'
$Notes[$Sessions[4].SessionId] = 'blocked on mogol/flutter_secure_storage#579; pixel-8 tester confirms fix'

# Demo settings.json — overlays the user's real settings during screenshot mode.
# Mix of Quick Launch entries demonstrating every feature: hotkeys (some null),
# --continue toggle, shell override, model override. View mode set to Compact
# (visually balanced) so the tray hero shot lands well; you can still cycle
# Mini/Compact/Full via the tray menu during the screenshot session.
$DemoSettings = [ordered]@{
    quickLaunchEntries          = @(
        [ordered]@{ name = 'demo-blog';       folderPath = 'C:\Code\demo-blog';       hotkey = 'Win+Alt+1'; continueLastSession = $true;  shellOverride = 'PowerShell'; modelOverride = $null }
        [ordered]@{ name = 'api-gateway';     folderPath = 'C:\Code\api-gateway';     hotkey = 'Win+Alt+2'; continueLastSession = $false; shellOverride = 'Cmd';        modelOverride = $null }
        [ordered]@{ name = 'invoicing-app';   folderPath = 'C:\Code\invoicing-app';   hotkey = 'Win+Alt+3'; continueLastSession = $false; shellOverride = $null;        modelOverride = 'opus' }
        [ordered]@{ name = 'mobile-app';      folderPath = 'C:\Code\mobile-app';      hotkey = 'Win+Alt+4'; continueLastSession = $false; shellOverride = $null;        modelOverride = $null }
        [ordered]@{ name = 'dotfiles';        folderPath = 'C:\Users\dev\dotfiles';   hotkey = $null;       continueLastSession = $false; shellOverride = 'GitBash';    modelOverride = 'haiku' }
        [ordered]@{ name = 'monorepo-tools';  folderPath = 'C:\Code\monorepo-tools';  hotkey = 'Win+Alt+5'; continueLastSession = $true;  shellOverride = $null;        modelOverride = 'opus' }
    )
    enableMainHotkey            = $true
    widgetToggleHotkey          = 'Win+LAlt+C'
    sessionBrowserHotkey        = 'Win+LAlt+S'
    promptLibraryHotkey         = 'Win+LAlt+P'
    permissionManagerHotkey     = 'Win+LAlt+N'
    claudeConfigHotkey          = 'Win+LAlt+K'
    agentsSkillsHotkey          = 'Win+LAlt+A'
    compactAggressiveness       = 'Balanced'
    enableCompactNotifications  = $true
    enablePermissionSuggestions = $true
    showActiveSessions          = $true
    checkForUpdatesOnStartup    = $true
    customCriticalPercent       = 75
    customWarningPercent        = 50
    widgetViewMode              = 'Compact'
    preferredShell              = 'Auto'
}

function Get-IsoTimestamp([DateTime]$dt) {
    return $dt.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
}

function New-DemoJsonl($session) {
    $lines = @()
    $usage = $UsagePresets[$session.Usage]
    $turns = $session.Prompts.Count
    # First turn starts (MinutesAgo + turns*2) ago, each turn is 2 minutes apart.
    # The trailing "Done." line pins LastSeen exactly at -MinutesAgo.
    $startTime = (Get-Date).ToUniversalTime().AddMinutes(-($session.MinutesAgo + $turns * 2))

    for ($i = 0; $i -lt $turns; $i++) {
        $userTime = $startTime.AddMinutes($i * 2)
        $asstTime = $userTime.AddSeconds(3 + ($i * 4))

        $userObj = [ordered]@{
            type      = 'user'
            sessionId = $session.SessionId
            timestamp = Get-IsoTimestamp $userTime
            cwd       = $session.Cwd
            gitBranch = $session.Branch
            message   = [ordered]@{
                role    = 'user'
                content = @(@{ type = 'text'; text = $session.Prompts[$i] })
            }
        }
        $lines += ($userObj | ConvertTo-Json -Compress -Depth 10)

        $asstObj = [ordered]@{
            type      = 'assistant'
            sessionId = $session.SessionId
            timestamp = Get-IsoTimestamp $asstTime
            message   = [ordered]@{
                role    = 'assistant'
                model   = $session.Model
                content = @(@{ type = 'text'; text = $session.Replies[$i] })
                usage   = [ordered]@{
                    input_tokens                = $usage.Input
                    output_tokens               = $usage.Out
                    cache_read_input_tokens     = [int]($usage.CacheRead * ($i + 1))
                    cache_creation_input_tokens = $usage.CacheCreate
                }
            }
        }
        $lines += ($asstObj | ConvertTo-Json -Compress -Depth 10)

        # /compact marker after turn 4 if this session is configured for it.
        if ($session.Compact -and $i -eq 3) {
            $compactObj = [ordered]@{
                type            = 'compact'
                sessionId       = $session.SessionId
                timestamp       = Get-IsoTimestamp $asstTime.AddSeconds(2)
                compactMetadata = [ordered]@{
                    trigger   = 'auto'
                    preTokens = 150000
                }
            }
            $lines += ($compactObj | ConvertTo-Json -Compress -Depth 10)
        }
    }

    # Trailing line pins LastSeen exactly at -MinutesAgo so the "Last activity"
    # column shows the value we want regardless of how the prior turns spaced.
    $finalTime = (Get-Date).ToUniversalTime().AddMinutes(-$session.MinutesAgo)
    $finalObj = [ordered]@{
        type      = 'assistant'
        sessionId = $session.SessionId
        timestamp = Get-IsoTimestamp $finalTime
        message   = [ordered]@{
            role    = 'assistant'
            model   = $session.Model
            content = @(@{ type = 'text'; text = 'Done.' })
            usage   = [ordered]@{
                input_tokens                = 40
                output_tokens               = 5
                cache_read_input_tokens     = 0
                cache_creation_input_tokens = 0
            }
        }
    }
    $lines += ($finalObj | ConvertTo-Json -Compress -Depth 10)

    return $lines
}

function Enable-ScreenshotMode {
    Assert-WidgetNotRunning

    if (Test-Path $ProjectsBackup) {
        throw @"
Backup folder already exists at:
  $ProjectsBackup
A previous -Enable wasn't followed by -Disable. Resolve manually before continuing.
"@
    }

    Write-Host 'Backing up real Claude projects folder...' -ForegroundColor Cyan
    if (Test-Path $ProjectsDir) {
        Move-Item -Path $ProjectsDir -Destination $ProjectsBackup
    }
    New-Item -ItemType Directory -Path $ProjectsDir -Force | Out-Null

    Write-Host 'Backing up real Sidekick overlay files...' -ForegroundColor Cyan
    foreach ($name in $OverlayFiles) {
        $src = Join-Path $AppData $name
        if (Test-Path $src) {
            Move-Item -Path $src -Destination "$src.screenshot-backup"
        }
    }

    Write-Host 'Writing 8 demo sessions...' -ForegroundColor Cyan
    foreach ($s in $Sessions) {
        $folder = Join-Path $ProjectsDir $s.Slug
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        $file = Join-Path $folder "$($s.SessionId).jsonl"
        New-DemoJsonl $s | Set-Content -Path $file -Encoding UTF8
    }

    Write-Host 'Writing demo overlay files (favorites, colors, notes)...' -ForegroundColor Cyan
    if (-not (Test-Path $AppData)) {
        New-Item -ItemType Directory -Path $AppData -Force | Out-Null
    }
    # ConvertTo-Json -AsArray ensures a 1-element favorites list still serializes
    # as a JSON array, but PS 5.1 doesn't have -AsArray — wrap in single-element
    # array literal via the unary comma operator for the 2-element case here.
    ($Favorites | ConvertTo-Json -Compress) | Set-Content -Path (Join-Path $AppData 'favorites.json') -Encoding UTF8
    ($Colors    | ConvertTo-Json -Compress) | Set-Content -Path (Join-Path $AppData 'session-colors.json') -Encoding UTF8
    ($Notes     | ConvertTo-Json -Compress) | Set-Content -Path (Join-Path $AppData 'session-notes.json') -Encoding UTF8

    Write-Host 'Writing demo settings.json (Quick Launch entries + view-mode defaults)...' -ForegroundColor Cyan
    ($DemoSettings | ConvertTo-Json -Depth 5) | Set-Content -Path (Join-Path $AppData 'settings.json') -Encoding UTF8

    Write-Host ''
    Write-Host 'Screenshot mode ENABLED.' -ForegroundColor Green
    Write-Host 'Start the widget and take your screenshots.' -ForegroundColor Yellow
    Write-Host 'When done:  .\screenshot-mode.ps1 -Disable' -ForegroundColor Yellow
}

function Disable-ScreenshotMode {
    Assert-WidgetNotRunning

    if (-not (Test-Path $ProjectsBackup)) {
        throw "No backup found at $ProjectsBackup — nothing to restore. Was -Enable run?"
    }

    Write-Host 'Removing demo sessions...' -ForegroundColor Cyan
    if (Test-Path $ProjectsDir) {
        Remove-Item -Path $ProjectsDir -Recurse -Force
    }

    Write-Host 'Restoring real Claude projects folder...' -ForegroundColor Cyan
    Move-Item -Path $ProjectsBackup -Destination $ProjectsDir

    Write-Host 'Restoring real Sidekick overlay files...' -ForegroundColor Cyan
    foreach ($name in $OverlayFiles) {
        $demo   = Join-Path $AppData $name
        $backup = "$demo.screenshot-backup"
        if (Test-Path $demo) {
            Remove-Item -Path $demo -Force
        }
        if (Test-Path $backup) {
            Move-Item -Path $backup -Destination $demo
        }
    }

    Write-Host ''
    Write-Host 'Screenshot mode DISABLED. Your real data is back.' -ForegroundColor Green
}

if ($Enable) {
    Enable-ScreenshotMode
}
elseif ($Disable) {
    Disable-ScreenshotMode
}
else {
    Get-Help $PSCommandPath -Detailed
}
