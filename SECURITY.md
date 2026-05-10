# Security Policy

## Reporting a Vulnerability

Please report security issues **privately** via GitHub Security Advisories:

→ https://github.com/RafalZG/claude-sessions-sidekick/security/advisories/new

This keeps the report private until a fix is ready and gives a clean audit trail. **Do not open a public issue** for security concerns.

## What to expect

- Acknowledgement within a few days
- Coordinated disclosure once a fix is shipped
- Credit in the release notes (unless you prefer to remain anonymous)

## Scope

In scope:
- Code execution from a maliciously-crafted `settings.json`, session JSONL, or other on-disk file the app reads
- Privilege escalation
- Sensitive data leaks (e.g. accidental upload of session contents to a remote server — note that this app currently makes no outbound calls except the official Claude Code usage API)

Out of scope:
- Issues requiring physical access to a logged-in machine (the app trusts the user it runs as)
- Issues in Claude Code itself — please report those to Anthropic
- Cosmetic / DoS in the local UI alone (e.g. hung tray menu)

## Notes on the OAuth client_id

The app authenticates against the Claude Code usage API using the same OAuth `client_id` that the official Claude Code CLI uses. This is a public client identifier published by Anthropic — it is not a secret, and it is hardcoded in the source.
