# BeszelAgentManager 4

BeszelAgentManager is a Windows desktop application for installing, configuring, updating, and monitoring the [Beszel](https://beszel.dev) agent.

Version 4 is a native .NET 10 and WinUI 3 application. Routine administrative actions are performed by a secured LocalSystem background service, so starting, stopping, updating, and configuring the agent does not repeatedly show UAC prompts.

## Features

- Install, update, roll back, force-reinstall, or uninstall the Beszel agent.
- Configure `KEY`, `TOKEN`, `HUB_URL`, `LISTEN`, and current Beszel v0.18.8 advanced environment variables.
- Start, stop, and restart the agent from the UI or tray.
- Apply NSSM service and Windows Firewall settings.
- Automatically switch to an IP fallback when primary Hub DNS fails, then restore the primary after five successful checks.
- Optionally reduce unreachable-Hub WebSocket retries with a persistent 1-to-60-minute backoff.
- Schedule automatic agent updates, periodic restarts, and daily log rotation in the background service.
- View manager and agent logs, rotate logs, and create a redacted support bundle.
- Reset the agent fingerprint.
- Install, roll back, or force-reinstall manager releases through a checksum-verified installer flow.
- Optional manager-update notifications, prerelease checks, skipped versions, and tray status.
- Optional Windows Defender exclusion with explicit consent.
- Third-party antivirus allowlist instructions covering application, data, and process paths.
- Start hidden with Windows, close to tray, open the Hub URL, and display live service state.

## Supported systems

- Windows 10 version 1809 or later, x64
- Windows 11, x64
- Windows Server 2019, 2022, or 2025 with Desktop Experience, x64
- Windows Server Core is not supported because the manager requires a graphical desktop session
- Administrator access for initial installation, upgrade, uninstall, and **Edit service…**
- Network access to GitHub releases and the configured Beszel Hub

## Installation

Choose one installer from the project’s GitHub release and download `SHA256SUMS.txt`:

- `BeszelAgentManagerSetup.exe` — Bundled edition; includes the .NET 10 runtime.
- `BeszelAgentManagerSetup-Lite.exe` — Lite edition; smaller download for systems that already have Microsoft .NET 10 Desktop Runtime x64. If it is missing, setup offers to open Microsoft’s download page and stops until the runtime is installed.

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

Optional WebSocket offline backoff is configured under **Extra**. After 12 consecutive `WebSocket connection failed` events, the background service pauses the agent and retries after 1, 2, 5, 10, 30, then 60 minutes. A successful connection immediately resets the delay. A Windows network-address change bypasses the current delay after a three-second trailing debounce, allowing adapters to settle before retrying. Disabling the option resumes an agent that the manager paused. Scheduled agent updates and periodic restarts are deferred while the backoff is active.

`EXIT_ON_DNS_ERROR` is an alternative agent-owned retry strategy and cannot be enabled together with manager WebSocket offline backoff.

Last-run and next-due state is persisted in `background-runtime-state.json`. Enabling a schedule starts its interval from the enable time rather than running immediately.

## Updates

Agent assets are selected only from the official Beszel GitHub repository. Every install, update, rollback, and scheduled update downloads the release's versioned checksum file and verifies `beszel-agent_windows_amd64.zip` with SHA-256 before extraction. Missing or mismatched checksums are rejected.

Manager updates accept only a selected release tag from the hardcoded project repository. Standard installations keep using the standard installer and Lite installations keep using the Lite installer. Lite appears beside the manager version in the UI; the standard edition remains plain `BeszelAgentManager`. The background service downloads the exact installer and `SHA256SUMS.txt` assets into a restricted staging directory, verifies the checksum, rejects invalid paths and reparse points, and launches Inno Setup silently after the UI exits. Authenticode is also required when a release is signed.

## Migration from 3.1.0

The v4 installer upgrades an existing 3.1.0 installation in place and preserves:

- configuration and encrypted GitHub token;
- agent environment and service state;
- agent executable and historical logs;
- update, restart, and startup preferences.

The legacy autostart executable path is migrated to the v4 `app` directory while preserving hidden or visible startup behavior. The agent’s NSSM service path is migrated to a stable, quoted ProgramData path.

Normal v4 upgrades use a generated SHA-256 manifest: unchanged application files are retained, changed or missing files are replaced, obsolete manifest-owned files are removed, and every installed file is verified before the background service restarts. A full application-directory refresh is reserved for v3 migration, an incomplete layout, or an explicit rollback.

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

Build the Bundled WinUI application:

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

For Lite, rebuild with `--self-contained false`, stage the output in `build\winui-lite-dist`, and compile with `/DLiteInstaller`. This produces:

```text
installer-dist\BeszelAgentManagerSetup-Lite.exe
```

## Repository layout

```text
src/BeszelAgentManager.WinUI/   WinUI desktop application
src/BeszelAgentManager.Helper/ LocalSystem service and privileged actions
src/BeszelAgentManager.Core/   Shared broker and failover logic
tests/                         Unit and guarded integration validation
installer/                     Inno Setup installer
docs/                          Architecture, security, migration, and recovery
```

## Credits

- BeszelAgentManager: Verhoef
- Beszel: [beszel.dev](https://beszel.dev)
