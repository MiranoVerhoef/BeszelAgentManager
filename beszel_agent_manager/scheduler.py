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
    AUTO_RESTART_TASK_NAME,

)
from .util import log

_restart_task_missing_logged = False

if os.name == "nt":
    CREATE_NO_WINDOW = subprocess.CREATE_NO_WINDOW
else:
    CREATE_NO_WINDOW = 0


def _resolve_task_executable() -> str:
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

$serviceName = "{AGENT_SERVICE_NAME}"
$agentPath   = "{AGENT_EXE_PATH}"
$logDir      = "$env:ProgramData\\{PROJECT_NAME}"
$logFile     = Join-Path $logDir "update.log"
$lockFile    = Join-Path $logDir "update.lock"

function Log([string]$m) {{
  try {{
    $ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    Add-Content -Path $logFile -Value "[$ts] $m"
  }} catch {{ }}
}}

function GetPid() {{
  try {{
    $out = & sc.exe queryex "$serviceName" 2>$null
    foreach ($l in $out) {{
      if ($l -match '^\s*PID\s*:\s*(\d+)') {{ return [int]$Matches[1] }}
    }}
  }} catch {{ }}
  return 0
}}

function WaitStopped([int]$seconds) {{
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) {{
    try {{
      $s = Get-Service -Name $serviceName -ErrorAction Stop
      if ($s.Status -eq 'Stopped') {{ return $true }}
    }} catch {{ return $true }}
    Start-Sleep -Seconds 1
  }}
  return $false
}}

function WaitRunning([int]$seconds) {{
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) {{
    try {{
      $s = Get-Service -Name $serviceName -ErrorAction Stop
      if ($s.Status -eq 'Running') {{ return $true }}
    }} catch {{ }}
    Start-Sleep -Seconds 1
  }}
  return $false
}}

function ForceKill() {{
  $pid = GetPid
  if ($pid -gt 0) {{
    Log "Service stuck; force-killing PID $pid"
    try {{ taskkill /PID $pid /T /F | Out-Null }} catch {{ }}
  }} else {{
    Log "Service stuck; PID not found; trying taskkill by image"
    try {{ taskkill /IM nssm.exe /T /F | Out-Null }} catch {{ }}
    try {{ taskkill /IM beszel-agent.exe /T /F | Out-Null }} catch {{ }}
  }}
}}

if (-not (Test-Path $logDir)) {{ New-Item -ItemType Directory -Path $logDir | Out-Null }}
if (-not (Test-Path $agentPath)) {{ exit 0 }}

try {{
  if (Test-Path $lockFile) {{
    $age = (Get-Item $lockFile).LastWriteTime
    if ($age -gt (Get-Date).AddMinutes(-60)) {{
      Log "Update skipped: previous run still active/recent (lock present)"
      exit 0
    }}
  }}
  Set-Content -Path $lockFile -Value (Get-Date).ToString('o') -Encoding ASCII
}} catch {{ }}

try {{
  Log "Update task starting"

  try {{ Stop-Service -Name $serviceName -ErrorAction SilentlyContinue -NoWait }} catch {{ }}
  if (-not (WaitStopped 15)) {{
    ForceKill
    [void](WaitStopped 10)
  }}

  $p = Start-Process -FilePath $agentPath -ArgumentList 'update' -NoNewWindow -PassThru
  if (-not (Wait-Process -Id $p.Id -Timeout 600 -ErrorAction SilentlyContinue)) {{
    Log "Agent update timed out; killing PID $($p.Id)"
    try {{ taskkill /PID $p.Id /T /F | Out-Null }} catch {{ }}
  }}
  try {{
    $out = Get-Content -Path $p.StandardOutputFileName -ErrorAction SilentlyContinue
  }} catch {{ }}
  Log "Agent update finished (exit code: $($p.ExitCode))"

}} catch {{
  Log "ERROR during update: $($_.Exception.Message)"
}} finally {{
  try {{ Start-Service -Name $serviceName -ErrorAction SilentlyContinue }} catch {{ }}
  if (-not (WaitRunning 20)) {{
    Log "Service did not reach RUNNING after update"
  }} else {{
    Log "Service running after update"
  }}
  try {{ Remove-Item -Force -Path $lockFile -ErrorAction SilentlyContinue }} catch {{ }}
}}
"""
    UPDATE_SCRIPT_PATH.write_text(script, encoding="utf-8")
    log(f"Wrote update script to {UPDATE_SCRIPT_PATH}")


def create_or_update_update_task(days_interval: int) -> None:
    if days_interval < 1:
        days_interval = 1
    exe = _resolve_task_executable()

    if os.path.basename(exe).lower() in ("python.exe", "pythonw.exe"):
        tr = f'"{exe}" -m beszel_agent_manager.main --auto-update-agent'
    else:
        tr = f'"{exe}" --auto-update-agent'

    cmd = [
        "schtasks",
        "/Create",
        "/F",
        "/SC", "DAILY",
        "/MO", str(days_interval),
        "/TN", AUTO_UPDATE_TASK_NAME,
        "/RL", "HIGHEST",
        "/RU", "SYSTEM",
        "/TR", tr,
    ]
    kwargs = {"check": False, "capture_output": True, "text": True}
    if CREATE_NO_WINDOW:
        kwargs["creationflags"] = CREATE_NO_WINDOW
    subprocess.run(cmd, **kwargs)
    log(f"Created/updated scheduled task {AUTO_UPDATE_TASK_NAME} (every {days_interval} day(s)).")


def ensure_update_task(days_interval: int) -> None:
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




def create_or_update_agent_log_rotate_task(start_time: str = "00:05") -> None:
    exe = _resolve_task_executable()

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




def create_or_update_periodic_restart_task(hours_interval: int, start_time: str = "00:10") -> None:
    if hours_interval < 1:
        hours_interval = 1
    if hours_interval > 168:
        hours_interval = 168

    exe = _resolve_task_executable()
    if os.path.basename(exe).lower() in ("python.exe", "pythonw.exe"):
        tr = f'"{exe}" -m beszel_agent_manager.main --restart-agent-service'
    else:
        tr = f'"{exe}" --restart-agent-service'

    cmd = [
        "schtasks",
        "/Create",
        "/F",
        "/SC", "HOURLY",
        "/MO", str(hours_interval),
        "/TN", AUTO_RESTART_TASK_NAME,
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
        log(f"Created/updated scheduled task {AUTO_RESTART_TASK_NAME} (every {hours_interval} hour(s)).")
    else:
        log(f"Failed to create scheduled task {AUTO_RESTART_TASK_NAME}: {cp.stderr.strip()}")


def ensure_periodic_restart_task(hours_interval: int) -> None:
    create_or_update_periodic_restart_task(hours_interval)


def delete_periodic_restart_task() -> None:
    global _restart_task_missing_logged
    cmd = ["schtasks", "/Delete", "/TN", AUTO_RESTART_TASK_NAME, "/F"]
    kwargs = {"check": False, "capture_output": True, "text": True}
    if CREATE_NO_WINDOW:
        kwargs["creationflags"] = CREATE_NO_WINDOW
    cp = subprocess.run(cmd, **kwargs)
    if cp.returncode == 0:
        log(f"Deleted scheduled task {AUTO_RESTART_TASK_NAME}.")
    else:
        combined = f"{cp.stdout or ''}\\n{cp.stderr or ''}".lower()

        not_found_markers = (
            "cannot find the file",
            "does not exist",
            "cannot find",
            "not found",
            "kan het opgegeven bestand niet vinden",
            "het systeem kan het opgegeven bestand niet vinden",
            "kan het opgegeven pad niet vinden",
            "het systeem kan het opgegeven pad niet vinden",
            "bestaat niet",
        )

        if any(m in combined for m in not_found_markers):
            if not _restart_task_missing_logged:
                log(f"Scheduled task {AUTO_RESTART_TASK_NAME} not found; nothing to delete.")
        else:
            err = (cp.stderr or cp.stdout or "").strip()
            log(f"Failed to delete scheduled task {AUTO_RESTART_TASK_NAME}: {err}")


