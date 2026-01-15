# BeszelAgentManager

BeszelAgentManager is a Windows app that makes it easy to **install, configure, update, and manage the Beszel agent**.

Vibe Coding is used.

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
  - Requests Windows Defender exclusion for `C:\Program Files\BeszelAgentManager`
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

## Build from source (for developers)

Prerequisites:
- Windows 10/11 (64-bit)
- Python 3.10+
- Python packages: `pyinstaller`, `requests`
- Optional (tray icon): `pystray`, `Pillow`

Recommended build with spec:
```powershell
python -m venv .venv
.\.venv\Scripts\activate
pip install pyinstaller requests
pip install pystray pillow

pyinstaller BeszelAgentManager.spec
```

Output:
```text
dist\BeszelAgentManager.exe
```

Manual one-liner build (alternative):
```powershell
pyinstaller --onefile --noconsole ^
  --icon=BeszelAgentManager_icon.ico ^
  --version-file=file_version_info.txt ^
  --add-data "BeszelAgentManager_icon.ico;." ^
  --add-data "BeszelAgentManager_icon_512.png;." ^
  -n BeszelAgentManager beszel_agent_manager\main.py
```

## Credits

- BeszelAgentManager: Verhoef  
- Beszel (upstream): https://beszel.dev
