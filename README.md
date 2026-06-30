# BeszelAgentManager

BeszelAgentManager is a Windows app that makes it easy to **install, configure, update, and manage the Beszel agent**.

Vibe Coding is used.
<img width="1000" height="810" alt="Schermafbeelding 2026-03-18 142806" src="https://github.com/user-attachments/assets/01192507-9b3d-4fa7-9107-c1dd825cdd8b" />

### Windows binaries for BeszelAgentManager are digitally signed through the SignPath Foundation.

## What it does

- Installs the Beszel agent to:  
  `C:\Program Files\Beszel-Agent\beszel-agent.exe`

- Runs the agent as a Windows Service (via NSSM):
  - Service name: `BeszelAgentManager`
  - Display name (Services.msc): **Beszel Agent**
  - Downloads NSSM automatically if needed

- Lets you configure the agent connection:
  - `KEY`, `TOKEN`, `HUB_URL`
  - Optional `LISTEN` (can be enabled/disabled)
  - Optional extra Beszel environment variables (advanced)

- Firewall support:
  - Can add/remove the Windows Firewall rule for the listen port when enabled

- Updates & version management:
  - **Update agent / Update manager**: checks GitHub and updates if a newer version exists
  - **Manage Agent Version… / Manage Manager Version…**: choose a specific version to install (upgrade or rollback), or force reinstall

- Logging built into the UI:
  - Manager log viewer + debug logging toggle
  - Agent log viewer + daily agent logs

- Tray support:
  - Optional “start with Windows” (tray always available)
  - Tray menu for quick actions (open app, open hub URL, service actions, update, exit)

- Safety and quality-of-life:
  - Requests Administrator rights when required (service/firewall/tasks)
  - Can relocate itself to `C:\Program Files\BeszelAgentManager` for a clean install location
  - Creates a Start Menu shortcut (removes it on uninstall)
  - Can add a Windows Defender exclusion for `C:\Program Files\BeszelAgentManager` only after explicit user consent
  - Enforces a single running instance (with an option to close the old one)
  - Uninstall removes service, firewall rule, tasks, agent files, and Start Menu shortcut

## Logs

- Manager logs:  
  `C:\ProgramData\BeszelAgentManager\manager.log`

- Agent logs (viewable in the app):  
  `C:\ProgramData\BeszelAgentManager\agent_logs\`  
  Includes daily rotated logs (`YYYY-MM-DD.txt`) plus the latest log.

## Using the app

1. Run `BeszelAgentManager.exe` (accept the UAC prompt if requested).
2. On the **Connection** tab, fill in:
   - **Key** (`KEY`)
   - **Token** (`TOKEN`) (optional)
   - **Hub URL** (`HUB_URL`)
   - **Listen port** (optional; you can enable/disable it)
3. Use the bottom buttons:
   - **Install agent**: installs the agent + service and applies configured settings
   - **Update agent**: updates the agent if a newer version exists
   - **Manage Agent Version…**: pick a specific version (rollback/upgrade) or force reinstall
   - **Update manager**: updates BeszelAgentManager if a newer version exists
   - **Manage Manager Version…**: pick a specific version (rollback/upgrade) or force reinstall
   - **Apply settings**: reapplies configuration without reinstalling everything
   - **Uninstall agent**: removes everything created by the manager

The status bar shows:
- Your detected agent version (when available)
- Hub reachability status (**Reachable / Unreachable / Not configured**) based on the configured `HUB_URL`

## GitHub authentication (optional)

BeszelAgentManager checks GitHub for Beszel agent and manager releases. Unauthenticated GitHub API requests can be rate limited, especially on shared networks or when checking updates often.

You can add a GitHub personal access token to raise the API limit:

1. Go to https://github.com/settings/tokens
2. Choose **Tokens (classic)**.
3. Create a token with **no scopes selected**. Public release checks do not need repository or admin permissions.
4. Copy the token.
5. In BeszelAgentManager, open **GitHub Authentication** and paste the token.

The token is stored encrypted with Windows DPAPI. If the `GITHUB_TOKEN` environment variable is set, it overrides the saved token.

## Build from source (for developers)

Prerequisites:
- Windows 10/11 (64-bit)
- Python 3.10+
- Inno Setup 6
- Python packages: `nuitka`, `ordered-set`, `zstandard`, `requests`
- Optional (tray icon): `pystray`, `Pillow`

Recommended standalone build:
```powershell
python -m venv .venv
.\.venv\Scripts\activate
pip install nuitka ordered-set zstandard requests
pip install pystray pillow

python -m nuitka `
  --standalone `
  --enable-plugin=tk-inter `
  --windows-console-mode=disable `
  --windows-icon-from-ico=BeszelAgentManager_icon.ico `
  --include-data-files=BeszelAgentManager_icon.ico=BeszelAgentManager_icon.ico `
  --include-data-files=BeszelAgentManager_icon_512.png=BeszelAgentManager_icon_512.png `
  --output-dir=build `
  --output-filename=BeszelAgentManager.exe `
  main.py
```

Build installer:
```powershell
iscc installer\BeszelAgentManager.iss /DAppVersion=3.0.0 /DDistDir="$PWD\build\main.dist"
```

Output:
```text
installer-dist\BeszelAgentManagerSetup.exe
```

## Credits

- BeszelAgentManager: Verhoef  
- Beszel (upstream): https://beszel.dev
