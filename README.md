# BeszelAgentManager 4

BeszelAgentManager is a Windows desktop application for installing, configuring, updating, and monitoring the [Beszel](https://beszel.dev) agent.

Version 4 is a native .NET 10 and WinUI 3 application. Routine administrative actions are performed by a secured LocalSystem background service, so starting, stopping, updating, and configuring the agent does not repeatedly show UAC prompts.

## Features

- Install, update, roll back, force-reinstall, or uninstall the Beszel agent.
- Configure `KEY`, `TOKEN`, `HUB_URL`, `LISTEN`, and supported advanced environment variables.
- Start, stop, and restart the agent from the UI or tray.
- Apply NSSM service and Windows Firewall settings.
- Automatically switch to an IP fallback when primary Hub DNS fails, then restore the primary after five successful checks.
- Schedule automatic agent updates, periodic restarts, and daily log rotation in the background service.
- View manager and agent logs, rotate logs, and create a redacted support bundle.
- Reset the agent fingerprint.
- Install, roll back, or force-reinstall manager releases through a checksum-verified installer flow.
- Optional manager-update notifications, prerelease checks, skipped versions, and tray status.
- Optional Windows Defender exclusion with explicit consent.
- Third-party antivirus allowlist instructions covering application, data, and process paths.
- Start hidden with Windows, close to tray, open the Hub URL, and display live service state.

## Supported systems

- Windows 10 or Windows 11, x64
- Administrator access for initial installation, upgrade, uninstall, and **Edit service…**
- Network access to GitHub releases and the configured Beszel Hub

## Installation

Download `BeszelAgentManagerSetup.exe` and `SHA256SUMS.txt` from the project’s GitHub release.

1. Verify the installer SHA-256 against `SHA256SUMS.txt`.
2. Run the installer and approve UAC.
3. Open the Connection page and enter the Hub connection settings.
4. Select **Install agent** or **Apply settings**.

The installer places the manager under:

```text
C:\Program Files\BeszelAgentManager
```

The agent is installed under:

```text
C:\Program Files\Beszel-Agent
```

Manager configuration and logs are stored under:

```text
C:\ProgramData\BeszelAgentManager
```

## Privileged background service

`BeszelAgentManager Background` runs as LocalSystem and exposes a versioned local named-pipe protocol. Installation records the installing Windows account’s SID in an administrator-protected policy file. Only that account and SYSTEM can connect, and the service verifies the connected client identity again before accepting an allowlisted action.

The broker handles:

- agent installation, updates, rollback, and uninstall;
- service start, stop, restart, NSSM configuration, and firewall changes;
- log rotation and fingerprint reset;
- optional Defender exclusion changes;
- verified manager installer staging;
- scheduled updates, restarts, DNS failover, and log rotation.

**Edit service…** intentionally remains a UAC action because the NSSM editor must run interactively in the signed-in user’s desktop session.

See [Architecture](docs/architecture.md) and [Security](docs/security.md) for details.

## Scheduling

Schedules run inside the background service; Windows Scheduled Tasks are not used.

- DNS fallback check: every minute
- Automatic agent update: configurable from 1 to 720 hours
- Periodic agent restart: configurable in minutes or hours, up to 168 hours
- Agent log rotation: daily at 00:05 local time

Last-run and next-due state is persisted in `background-runtime-state.json`. Enabling a schedule starts its interval from the enable time rather than running immediately.

## Updates

Agent assets are selected only from the official Beszel GitHub repository.

Manager updates accept only a selected release tag from the hardcoded project repository. The background service downloads the exact installer and `SHA256SUMS.txt` assets into a restricted staging directory, verifies the checksum, rejects invalid paths and reparse points, and launches Inno Setup silently after the UI exits. Authenticode is also required when a release is signed.

## Migration from 3.1.0

The v4 installer upgrades an existing 3.1.0 installation in place and preserves:

- configuration and encrypted GitHub token;
- agent environment and service state;
- agent executable and historical logs;
- update, restart, and startup preferences.

The legacy autostart executable path is migrated to the v4 `app` directory while preserving hidden or visible startup behavior. The agent’s NSSM service path is migrated to a stable, quoted ProgramData path.

Normal v4 upgrades use version-aware incremental replacement: unchanged versioned files are retained, same-version files are replaced only when their content differs, and debug symbols are not installed. A full application-directory refresh is reserved for v3 migration, an incomplete layout, or an explicit rollback.

## Uninstall behavior

Manager uninstall removes the WinUI application, broker service, broker policy, Defender exclusion, legacy scheduled tasks, shortcuts, autostart entry, update staging, and manager state. Silent uninstall preserves historical agent logs and the stable NSSM wrapper so an independently running agent is not broken.

Agent uninstall is a separate action in the manager and offers a keep/remove logs choice.

## Optional GitHub authentication

A GitHub personal access token with no scopes can be saved to increase public API rate limits. It is encrypted with Windows DPAPI for the current user. The `GITHUB_TOKEN` environment variable overrides the saved token.

## Build from source

Prerequisites:

- Windows 10/11 x64
- .NET 10 SDK
- Inno Setup 6
- PowerShell 7 recommended

Restore, test, and build:

```powershell
dotnet restore BeszelAgentManager.sln
dotnet test tests\BeszelAgentManager.Core.Tests\BeszelAgentManager.Core.Tests.csproj -c Release
dotnet build BeszelAgentManager.sln -c Release -p:Platform=x64
```

Build the self-contained WinUI application:

```powershell
dotnet build src\BeszelAgentManager.WinUI\BeszelAgentManager.WinUI.csproj `
  -c Release `
  -p:Platform=x64 `
  -r win-x64 `
  --self-contained true

New-Item -ItemType Directory -Path 'build\winui-dist' -Force | Out-Null
Copy-Item `
  'src\BeszelAgentManager.WinUI\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\*' `
  'build\winui-dist' `
  -Recurse -Force
Remove-Item 'build\winui-dist\BeszelAgentManager.Helper.*' -Force -ErrorAction SilentlyContinue
```

Place the official x64 NSSM 2.24 binary at `build\winui-dist\nssm.exe`, then compile the installer:

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" `
  installer\BeszelAgentManager.iss `
  /DAppVersion="$((Get-Content VERSION -Raw).Trim())" `
  /DDistDir="$((Resolve-Path 'build\winui-dist').Path)"
```

Output:

```text
installer-dist\BeszelAgentManagerSetup.exe
```

## Code signing

SignPath Foundation integration is reserved for the stable release. Treat RC artifacts as unsigned unless Windows reports a valid Authenticode signature.

The GitHub Actions release workflow performs the same publish, NSSM validation, installer build, and checksum generation. Pull requests produce a test installer artifact; stable and RC releases are controlled by `VERSION` and `RELEASE_CHANNEL`.

## Repository layout

```text
src/BeszelAgentManager.WinUI/   WinUI desktop application
src/BeszelAgentManager.Helper/ LocalSystem service and privileged actions
src/BeszelAgentManager.Core/   Shared broker and failover logic
tests/                         Unit and guarded integration validation
installer/                     Inno Setup installer
docs/                          Architecture, security, migration, and recovery
```

## Development status

Version `4.0.0` remains on the RC channel until the pull-request installer artifact and final release checklist pass.

## Credits

- BeszelAgentManager: Verhoef
- Beszel: [beszel.dev](https://beszel.dev)
