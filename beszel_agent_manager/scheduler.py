from __future__ import annotations
import subprocess
import os
import sys
from .constants import (
    AGENT_EXE_PATH,
    AGENT_SERVICE_NAME,
    UPDATE_SCRIPT_PATH,
    AUTO_UPDATE_TASK_NAME,
    PROJECT_NAME,
    AGENT_LOG_ROTATE_TASK_NAME,
    MANAGER_EXE_PATH,
)
from .util import log

if os.name == "nt":
    CREATE_NO_WINDOW = subprocess.CREATE_NO_WINDOW
else:
    CREATE_NO_WINDOW = 0


def _resolve_task_executable() -> str:
    """Return the executable to use in scheduled tasks.

    - In a frozen (PyInstaller) build, sys.executable is the manager .exe.
    - When running from source, fall back to Program Files install path if present.
    """
    exe = sys.executable
    if getattr(sys, "frozen", False):
        return exe

    try:
        if exe and exe.lower().endswith(".exe") and os.path.exists(exe):
            return exe
    except Exception:
        pass

    try:
        if MANAGER_EXE_PATH.exists():
            return str(MANAGER_EXE_PATH)
    except Exception:
        pass

    return exe


def write_update_script() -> None:
    UPDATE_SCRIPT_PATH.parent.mkdir(parents=True, exist_ok=True)
    script = f"""
$ErrorActionPreference = "Stop"

$agentPath = "{AGENT_EXE_PATH}"
$logDir   = "$env:ProgramData\\{PROJECT_NAME}"
$logFile  = Join-Path $logDir "update.log"

if (-not (Test-Path $logDir)) {{
    New-Item -ItemType Directory -Path $logDir | Out-Null
}}

if (-not (Test-Path $agentPath)) {{
    exit 0
}}

try {{
    Stop-Service -Name "{AGENT_SERVICE_NAME}" -ErrorAction SilentlyContinue

    $output = & $agentPath update 2>&1
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $entry = "[$timestamp] beszel-agent update output:`r`n$output`r`n---`r`n"
    $entry | Out-File -FilePath $logFile -Append -Encoding UTF8
}}
catch {{
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $entry = "[$timestamp] ERROR during update: $($_.Exception.Message)`r`n---`r`n"
    $entry | Out-File -FilePath $logFile -Append -Encoding UTF8
}}
finally {{
    try {{
        Start-Service -Name "{AGENT_SERVICE_NAME}" -ErrorAction SilentlyContinue
    }} catch {{ }}
}}
"""
    UPDATE_SCRIPT_PATH.write_text(script, encoding="utf-8")
    log(f"Wrote update script to {UPDATE_SCRIPT_PATH}")


def create_or_update_update_task(days_interval: int) -> None:
    if days_interval < 1:
        days_interval = 1
    write_update_script()
    cmd = [
        "schtasks",
        "/Create",
        "/F",
        "/SC", "DAILY",
        "/MO", str(days_interval),
        "/TN", AUTO_UPDATE_TASK_NAME,
        "/RL", "HIGHEST",
        "/RU", "SYSTEM",
        "/TR", f'powershell.exe -ExecutionPolicy Bypass -File "{UPDATE_SCRIPT_PATH}"',
    ]
    kwargs = {"check": False, "capture_output": True, "text": True}
    if CREATE_NO_WINDOW:
        kwargs["creationflags"] = CREATE_NO_WINDOW
    subprocess.run(cmd, **kwargs)
    log(f"Created/updated scheduled task {AUTO_UPDATE_TASK_NAME} (every {days_interval} day(s)).")


def ensure_update_task(days_interval: int) -> None:
    """Back-compat wrapper (older code calls ensure_update_task)."""
    create_or_update_update_task(days_interval)


def delete_update_task() -> None:
    cmd = ["schtasks", "/Delete", "/TN", AUTO_UPDATE_TASK_NAME, "/F"]
    kwargs = {"check": False, "capture_output": True, "text": True}
    if CREATE_NO_WINDOW:
        kwargs["creationflags"] = CREATE_NO_WINDOW
    cp = subprocess.run(cmd, **kwargs)
    if cp.returncode == 0:
        log(f"Deleted scheduled task {AUTO_UPDATE_TASK_NAME}.")
    else:
        stderr = (cp.stderr or "").lower()
        if "cannot find the file" in stderr or "does not exist" in stderr:
            log(f"Scheduled task {AUTO_UPDATE_TASK_NAME} not found; nothing to delete.")
        else:
            log(f"Failed to delete scheduled task {AUTO_UPDATE_TASK_NAME}: {cp.stderr.strip()}")


# ---------------------------------------------------------------------------
# Agent log rotation task (daily)
# ---------------------------------------------------------------------------


def create_or_update_agent_log_rotate_task(start_time: str = "00:05") -> None:
    """Create a daily scheduled task that rotates the agent log.

    This task runs the manager executable with --rotate-agent-logs.
    It is created to run as SYSTEM with highest privileges.
    """
    exe = _resolve_task_executable()

    # /TR must be a single string; quote paths to handle spaces.
    # When running from source, sys.executable is usually python.exe, so we must
    # invoke our module explicitly.
    if os.path.basename(exe).lower() in ("python.exe", "pythonw.exe"):
        tr = f'"{exe}" -m beszel_agent_manager.main --rotate-agent-logs'
    else:
        tr = f'"{exe}" --rotate-agent-logs'

    cmd = [
        "schtasks",
        "/Create",
        "/F",
        "/SC", "DAILY",
        "/TN", AGENT_LOG_ROTATE_TASK_NAME,
        "/RL", "HIGHEST",
        "/RU", "SYSTEM",
        "/ST", start_time,
        "/TR", tr,
    ]
    kwargs = {"check": False, "capture_output": True, "text": True}
    if CREATE_NO_WINDOW:
        kwargs["creationflags"] = CREATE_NO_WINDOW
    cp = subprocess.run(cmd, **kwargs)
    if cp.returncode == 0:
        log(f"Created/updated scheduled task {AGENT_LOG_ROTATE_TASK_NAME} (daily at {start_time}).")
    else:
        log(f"Failed to create scheduled task {AGENT_LOG_ROTATE_TASK_NAME}: {cp.stderr.strip()}")


def ensure_agent_log_rotate_task() -> None:
    """Ensure the daily rotation task exists (default 00:05)."""
    create_or_update_agent_log_rotate_task("00:05")


def delete_agent_log_rotate_task() -> None:
    cmd = ["schtasks", "/Delete", "/TN", AGENT_LOG_ROTATE_TASK_NAME, "/F"]
    kwargs = {"check": False, "capture_output": True, "text": True}
    if CREATE_NO_WINDOW:
        kwargs["creationflags"] = CREATE_NO_WINDOW
    cp = subprocess.run(cmd, **kwargs)
    if cp.returncode == 0:
        log(f"Deleted scheduled task {AGENT_LOG_ROTATE_TASK_NAME}.")
    else:
        stderr = (cp.stderr or "").lower()
        if "cannot find the file" in stderr or "does not exist" in stderr:
            log(f"Scheduled task {AGENT_LOG_ROTATE_TASK_NAME} not found; nothing to delete.")
        else:
            log(f"Failed to delete scheduled task {AGENT_LOG_ROTATE_TASK_NAME}: {cp.stderr.strip()}")


