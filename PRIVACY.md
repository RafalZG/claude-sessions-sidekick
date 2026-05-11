# Privacy Policy

**Last updated:** 2026-05-11

Claude Sessions Sidekick (the "App") does not collect, store, or transmit
any user data to the project author or any third party. This document
describes exactly what the App does on your machine.

## What the App reads on your machine

The App reads, locally, from:

- `%USERPROFILE%\.claude\` — Claude Code's own session JSONL files,
  project state, and OAuth credentials. The App parses these to populate
  the Session Browser, usage tracking, and related views.
- `%APPDATA%\ClaudeSessionsSidekick\` — the App's own settings, favorites,
  notes, color tags, prompt library, and logs. Created and maintained by
  the App.

All processing of these files happens locally. Nothing is uploaded.

## What the App sends over the network

The App makes **one** outbound network call:

- **Anthropic's Claude Code usage API** (under `*.anthropic.com`) — to
  populate the usage tracking display (5-hour rolling block, weekly
  Sonnet/Opus utilization). The App uses the OAuth token that Claude
  Code itself stores locally; no separate credentials are ever entered
  into the App, and no traffic from this call reaches the project
  author or any other party.

The App makes no other network requests. No analytics, no crash
reporting, no auto-update telemetry, no "phone home".

## What is NOT collected or transmitted

- No personally identifiable information
- No content of your Claude Code sessions (prompts, completions, file
  contents)
- No usage telemetry or analytics
- No crash reports
- No identifiers about you or your machine

## Auto-update

The App checks GitHub Releases for newer versions and, on request,
downloads and applies updates. This download is a standard HTTPS request
to `github.com` — GitHub may log this request per their own privacy
policy. The project author receives no information about this.

## Logs

Local diagnostic logs are written to
`%APPDATA%\ClaudeSessionsSidekick\app.log`. These stay on your machine.
They contain timestamps, error messages, and diagnostic info to help you
or a bug reporter triage issues. The logs may include file paths from
your `%USERPROFILE%\.claude\` directory. They are not transmitted.

## Source code

The full source code is available at
<https://github.com/RafalZG/claude-sessions-sidekick> under the MIT
license. The above claims are verifiable by reading the code — there is
no obfuscation and no third-party SDK that could relay data behind the
scenes.

## Contact

Questions about this privacy policy or about the App's data handling
should be raised as an issue on the GitHub repository.
