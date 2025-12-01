# BeszelAgentManager 1.6.0

BeszelAgentManager is a Windows GUI helper that installs and manages the Beszel agent.

- Installs agent to: `C:\\Program Files\\Beszel-Agent\\beszel-agent.exe`
- Creates/updates an NSSM-based Windows service:
  - Service name: `BeszelAgentManager`
  - Display name: `BeszelAgentManager`
- Automatically downloads NSSM if it is not present
- Configures `KEY`, `TOKEN`, `HUB_URL`, `LISTEN` and advanced environment variables
- Opens Windows Firewall for the listen port
- Optional automatic updates via Scheduled Tasks
- Logging tab with debug logging option
- Option to start BeszelAgentManager with Windows (tray always available)
- Tray menu with:
  - Open BeszelAgentManager
  - **Open hub URL** (uses the configured HUB URL)
  - Start/Stop/Restart service
  - Update agent now
  - Exit
- Service control buttons on the main Connection tab
- Shows the detected agent version in the status bar
- Shows the BeszelAgentManager version (`1.1.0`) in the GUI and in the EXE metadata
- Separate "Install agent" and "Update agent" actions
- On startup, automatically requests administrator rights and can move itself to
  `C:\\Program Files\\BeszelAgentManager` for a clean installation location
- Creates a Start Menu shortcut under the Windows app list, and removes it on uninstall
- Requests a Windows Defender exclusion for `C:\\Program Files\\BeszelAgentManager`
- Enforces a single running instance of the manager with an option to kill the old instance
- Uninstall button to remove service, firewall rule, task, agent files and the Start Menu shortcut
- "About" button for the manager (credit: Verhoef)
- "About Beszel" button with links to the upstream project and website
- Status bar now also shows best-effort **Hub** connectivity status (`Reachable` / `Unreachable` / `Not configured`)

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
