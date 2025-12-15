# BeszelAgentManager

BeszelAgentManager is a simple Windows app that helps you install, configure, and manage the Beszel agent on a Windows machine — without having to do everything manually.

What it does

Installs the Beszel agent to:
C:\Program Files\Beszel-Agent\beszel-agent.exe

Runs the agent as a Windows Service so it starts and stays running:

Service name: BeszelAgentManager

Shown in Services as: Beszel Agent

Lets you configure the agent connection:

KEY

TOKEN

HUB_URL

Optional LISTEN port

Optional extra Beszel environment settings (advanced options)

Firewall support for the listen port
You can enable/disable the listen port, and the app can add/remove the required Windows Firewall rule.

Update & version management

Update agent / Update manager: checks GitHub and updates if a newer version exists

Manage version menus: pick a specific version to install (upgrade or rollback), or force reinstall

Scheduled tasks (optional)

Can set up automatic update tasks (if enabled in the UI)

Logs (built in)

Manager logs are stored here:
C:\ProgramData\BeszelAgentManager\manager.log

Agent logs are viewable in the app and stored here:
C:\ProgramData\BeszelAgentManager\agent_logs\

Current log: beszel-agent.log

Daily logs: YYYY-MM-DD.txt

Easy controls in the app

Install agent: downloads the agent and sets everything up

Update agent: updates only the agent if there’s a newer version

Manage Agent Version…: choose a specific agent version or force reinstall

Update manager: updates the BeszelAgentManager app itself

Manage Manager Version…: choose a specific manager version or force reinstall

Apply settings: reapplies settings without reinstalling

Uninstall agent: removes the service, firewall rule, tasks, and installed files

Tray icon (runs in the background)

When enabled, BeszelAgentManager can live in your system tray so you can quickly:

Open BeszelAgentManager

Open your configured Hub URL

Start / Stop / Restart the service

Trigger an update check

Exit

Status indicators

The status bar shows:

Your installed agent version (when detected)

Hub reachability status (Reachable / Unreachable / Not configured) based on your HUB_URL

Notes

Because it installs services, firewall rules, and scheduled tasks, BeszelAgentManager will request Administrator permissions when needed.

Credit: Verhoef

## Prerequisites

- Windows 10/11 64-bit
- Python 3.10+ (for building)
- Python packages: `pyinstaller`, `requests`
- Optional (for tray icon): `pystray`, `Pillow`

## Build with the .spec (recommended for 1.1.0)

From the project root (where `BeszelAgentManager.spec` lives):

```powershell
cd C:\Projects\BeszelAgentManager-1.1.0
python -m venv .venv
.\.venv\Scripts\activate
pip install pyinstaller requests
pip install pystray pillow

pyinstaller BeszelAgentManager.spec
```

This will produce:

```text
dist\BeszelAgentManager.exe
```

The resulting EXE will have:

- Embedded icon
- Version resource (`1.1.0`, ProductName = BeszelAgentManager, Company = Verhoef)
- No console window

## Manual one-liner build (alternative)

If you prefer not to use the spec file, you can also run:

```powershell
pyinstaller --onefile --noconsole ^
  --icon=BeszelAgentManager_icon.ico ^
  --version-file=file_version_info.txt ^
  --add-data "BeszelAgentManager_icon.ico;." ^
  --add-data "BeszelAgentManager_icon_512.png;." ^
  -n BeszelAgentManager beszel_agent_manager\main.py
```

The EXE will be created at `dist\BeszelAgentManager.exe`.

## Usage

1. Run `BeszelAgentManager.exe` (accept the UAC prompt if requested).

2. On the *Connection* tab fill in:

   - **Key**: public key from your Beszel hub (`KEY`)
   - **Token**: optional registration token (`TOKEN`)
   - **Hub URL**: URL of your hub / monitoring instance (`HUB_URL`)
   - **Listen (port)**: listen port, default `45876`

   In the *Automatic updates* section you can enable a scheduled task for `beszel-agent update`.

   In the *Manager startup* section you can enable **Start BeszelAgentManager with Windows**
   so the GUI and tray icon are always available.

   In the *Service control* section you can Start, Stop or Restart the `BeszelAgentManager`
   service from the main window.

3. On the *Advanced env* tab you can set all additional environment variables used by the agent.

4. On the *Logging* tab:

   - Enable **debug logging** if you want detailed command output in the log.
   - Use **Open log folder** to inspect `manager.log` under `C:\\ProgramData\\BeszelAgentManager`.

5. Use the buttons at the bottom:

   - **Install agent**: downloads the agent, creates/updates the service and firewall rule,
     configures the scheduled task (if enabled) and ensures a Start Menu shortcut exists.
   - **Update agent**: re-downloads the agent binary and restarts the service.
   - **Apply settings**: reapplies configuration (environment variables, firewall, scheduled task)
     without re-downloading the agent, and also ensures the Start Menu shortcut exists.
   - **Uninstall agent**: removes the service, firewall rule, scheduled task, agent files and
     the Start Menu shortcut.

The agent version detected from the binary (or from the download URL as a fallback) is shown in
the status bar, next to the BeszelAgentManager version (`1.1.0`).

The status bar also shows a simple hub connectivity status based on the configured `HUB_URL`
(`Reachable`, `Unreachable` or `Not configured`). This is a best-effort HTTP check and does not
guarantee that this specific agent is registered in the hub, only that the hub URL is reachable.

Run the built EXE as Administrator (or accept its UAC prompt) so it can create services,
firewall rules, scheduled tasks, the Start Menu entry and the Defender exclusion.
