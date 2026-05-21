# winget/

Windows Package Manager (winget) manifests for Claude Sessions Sidekick.

Each release has its own subfolder named after the version. Each folder
contains the three YAML files winget-pkgs expects:

- `*.yaml` — version manifest (entry point)
- `*.installer.yaml` — installer details (URL, hash, switches)
- `*.locale.en-US.yaml` — metadata (name, description, tags, license)

## Install from a local manifest (before the PR is merged)

Anyone wanting to install via winget before our submission has been
accepted into the public winget-pkgs repository can install directly
from the local manifest:

```powershell
git clone https://github.com/RafalZG/claude-sessions-sidekick.git
cd claude-sessions-sidekick
winget install --manifest winget/1.0.0-rc5
```

This bypasses the public winget catalog and points winget straight at
our YAML. Same SHA256-verified download from GitHub Releases as the
public path will use once accepted.

## Submitting a new version to the public winget catalog

When a new release ships, repeat this flow for the new version folder.

1. **Update the manifests**

   Copy the previous version's folder, rename to the new version, then
   in each YAML update:
   - `PackageVersion` (in all three files)
   - `ReleaseDate` (installer.yaml)
   - `ReleaseNotesUrl` (locale.en-US.yaml + installer.yaml)
   - `InstallerUrl` (installer.yaml — points at the new release)
   - `InstallerSha256` (installer.yaml — recompute, see below)
   - `ReleaseNotes` text (locale.en-US.yaml)

   Recompute the SHA256 against the published Setup.exe:

   ```powershell
   $url = "https://github.com/RafalZG/claude-sessions-sidekick/releases/download/v1.0.0-rc6/ClaudeSessionsSidekick-win-Setup.exe"
   $tmp = "$env:TEMP\setup-check.exe"
   Invoke-WebRequest $url -OutFile $tmp
   (Get-FileHash -Algorithm SHA256 $tmp).Hash
   Remove-Item $tmp
   ```

2. **Validate locally**

   Install the winget validator once, then check the new manifests:

   ```powershell
   winget validate --manifest winget/1.0.0-rc6
   ```

   Any error here will also block the upstream PR — fix locally first.

3. **Sandbox-test the install**

   On a clean machine (or in Windows Sandbox), run the local-manifest
   install command from the section above. Confirm the app launches
   and the version reported in About matches the manifest.

4. **Open a PR to microsoft/winget-pkgs**

   - Fork [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs)
   - In your fork, copy `winget/1.0.0-rc6/*.yaml` to
     `manifests/r/RafalZG/ClaudeSessionsSidekick/1.0.0-rc6/`
   - Open a PR titled `New version: RafalZG.ClaudeSessionsSidekick version 1.0.0-rc6`
   - Their CI runs automated validation; community reviewers approve
     within 1-7 days

   For new packages (first submission) the title is `New package: ...`
   and review tends to take a bit longer.

## Why winget matters here

Direct download from GitHub Releases hits Windows SmartScreen on first
launch because the binary is unsigned. Winget bypasses that — the
installer hash is verified against the manifest and execution happens
in the context of the Microsoft-signed `winget.exe`, so no SmartScreen
prompt appears for the user.

For corporate environments using Microsoft Intune / Company Portal,
either path works:

- Users self-serve via `winget install RafalZG.ClaudeSessionsSidekick`
- IT pushes the same Setup.exe via Intune Win32 app (see main README
  "Install" section for the deployment command line)
