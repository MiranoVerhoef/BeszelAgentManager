from __future__ import annotations

import json
import os
import re
import ssl
import subprocess
import sys
from pathlib import Path
from typing import Dict, List, Optional, Tuple

from .constants import (
    MANAGER_ASSET_NAME,
    MANAGER_REPO,
    MANAGER_UPDATE_SCRIPT,
    MANAGER_UPDATES_DIR,
    PROJECT_NAME,
)
from .util import log


def _normalize_version(tag: Optional[str]) -> Optional[str]:
    if not tag:
        return None
    tag = str(tag).strip()
    if tag.lower().startswith("v"):
        tag = tag[1:].strip()
    m = re.search(r"\d+\.\d+\.\d+", tag)
    return m.group(0) if m else (tag or None)


def _version_tuple(v: str) -> Tuple[int, int, int]:
    parts = (v.split(".") + ["0", "0", "0"])[:3]
    try:
        return int(parts[0]), int(parts[1]), int(parts[2])
    except Exception:
        return 0, 0, 0


def _powershell_exe() -> str:
    sysroot = os.environ.get("SystemRoot", r"C:\\Windows")
    ps = os.path.join(sysroot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
    return ps if os.path.exists(ps) else "powershell.exe"


def _ps_escape_single(s: str) -> str:
    return s.replace("'", "''")


def _ps_run(command: str, timeout: int = 60) -> subprocess.CompletedProcess:
    ps = _powershell_exe()
    cmd = [ps, "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command]
    creationflags = subprocess.CREATE_NO_WINDOW if hasattr(subprocess, "CREATE_NO_WINDOW") else 0
    return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, creationflags=creationflags)


def _http_get_json(url: str, headers: Optional[Dict[str, str]] = None, timeout: int = 15) -> dict:
    headers = headers or {}

    # Try urllib first
    try:
        import urllib.request

        req = urllib.request.Request(url, headers=headers)
        ctx = ssl.create_default_context()
        with urllib.request.urlopen(req, timeout=timeout, context=ctx) as resp:
            return json.loads(resp.read().decode("utf-8", "replace"))
    except ssl.SSLCertVerificationError as exc:
        log(f"GitHub request SSL verify failed: {exc}. Falling back to PowerShell.")
    except Exception as exc:
        log(f"GitHub request failed: {exc}. Falling back to PowerShell.")

    # PowerShell fallback (Windows cert store)
    hdr = "@{}"
    if headers:
        parts = [f"'{_ps_escape_single(k)}'='{_ps_escape_single(v)}'" for k, v in headers.items()]
        hdr = "@{" + ";".join(parts) + "}"

    u = _ps_escape_single(url)
    ps_cmd = f"$h={hdr}; (Invoke-WebRequest -UseBasicParsing -Headers $h -Uri '{u}').Content"
    cp = _ps_run(ps_cmd, timeout=timeout)
    if cp.returncode != 0:
        raise RuntimeError((cp.stderr or cp.stdout or "").strip())
    return json.loads((cp.stdout or "").encode("utf-8", "ignore").decode("utf-8", "replace"))


def _download_file(url: str, dest: Path, headers: Optional[Dict[str, str]] = None, timeout: int = 60) -> None:
    headers = headers or {}
    dest.parent.mkdir(parents=True, exist_ok=True)

    # Try urllib first
    try:
        import urllib.request

        req = urllib.request.Request(url, headers=headers)
        ctx = ssl.create_default_context()
        with urllib.request.urlopen(req, timeout=timeout, context=ctx) as resp, dest.open("wb") as f:
            while True:
                b = resp.read(1024 * 1024)
                if not b:
                    break
                f.write(b)
        return
    except ssl.SSLCertVerificationError as exc:
        log(f"Download SSL verify failed: {exc}. Falling back to PowerShell.")
    except Exception as exc:
        log(f"Download failed: {exc}. Falling back to PowerShell.")

    u = _ps_escape_single(url)
    outp = _ps_escape_single(str(dest))
    ps_cmd = f"Invoke-WebRequest -UseBasicParsing -Uri '{u}' -OutFile '{outp}'"
    cp = _ps_run(ps_cmd, timeout=timeout)
    if cp.returncode != 0:
        raise RuntimeError((cp.stderr or cp.stdout or "").strip())


def fetch_latest_release() -> Optional[dict]:
    url = f"https://api.github.com/repos/{MANAGER_REPO}/releases/latest"
    headers = {"User-Agent": PROJECT_NAME, "Accept": "application/vnd.github+json"}
    data = _http_get_json(url, headers=headers, timeout=20)

    if data.get("draft") or data.get("prerelease"):
        # Should not happen for /latest, but keep it safe.
        return None

    tag = str(data.get("tag_name") or "").strip()
    version = _normalize_version(tag)
    body = str(data.get("body") or "")

    asset_url = None
    for a in (data.get("assets") or []):
        if str(a.get("name") or "").lower() == MANAGER_ASSET_NAME.lower():
            asset_url = a.get("browser_download_url")
            break

    if not version or not asset_url:
        return None

    return {
        "version": version,
        "tag": tag,
        "body": body,
        "download_url": asset_url,
        "published_at": data.get("published_at"),
    }


def fetch_stable_releases(limit: int = 50) -> List[dict]:
    url = f"https://api.github.com/repos/{MANAGER_REPO}/releases?per_page={limit}"
    headers = {"User-Agent": PROJECT_NAME, "Accept": "application/vnd.github+json"}
    data = _http_get_json(url, headers=headers, timeout=20)

    releases: List[dict] = []
    for r in data if isinstance(data, list) else []:
        if r.get("draft") or r.get("prerelease"):
            continue

        tag = str(r.get("tag_name") or "").strip()
        version = _normalize_version(tag)
        if not version:
            continue

        asset_url = None
        for a in (r.get("assets") or []):
            if str(a.get("name") or "").lower() == MANAGER_ASSET_NAME.lower():
                asset_url = a.get("browser_download_url")
                break

        if not asset_url:
            continue

        releases.append(
            {
                "version": version,
                "tag": tag,
                "body": str(r.get("body") or ""),
                "download_url": asset_url,
                "published_at": r.get("published_at"),
            }
        )

    # Sort newest-first by version tuple
    releases.sort(key=lambda x: _version_tuple(x["version"]), reverse=True)
    return releases


def is_update_available(current_version: str, latest_version: str) -> bool:
    return _version_tuple(latest_version) > _version_tuple(current_version)


def stage_download(release: dict, force: bool = False) -> Path:
    tag = release.get("tag") or release.get("version")
    version = release.get("version") or "unknown"
    download_url = release["download_url"]

    out_dir = MANAGER_UPDATES_DIR / str(version)
    out_dir.mkdir(parents=True, exist_ok=True)
    dest = out_dir / MANAGER_ASSET_NAME

    # Force re-download if requested.
    if force and dest.exists():
        try:
            dest.unlink()
        except Exception:
            pass

    log(f"Downloading manager {version} from {download_url}")
    _download_file(download_url, dest, headers={"User-Agent": PROJECT_NAME}, timeout=120)
    return dest


def _installed_manager_path() -> Path:
    program_files = os.environ.get("ProgramFiles", r"C:\\Program Files")
    return Path(program_files) / PROJECT_NAME / MANAGER_ASSET_NAME


def _write_update_script(pid: int, src_exe: Path, dst_exe: Path, args: List[str]) -> Path:
    MANAGER_UPDATE_SCRIPT.parent.mkdir(parents=True, exist_ok=True)
    # Build argument string for Start-Process
    arg_str = " ".join([f'"{a}"' for a in args])
    # Write into the same manager log file (ProgramData\BeszelAgentManager\manager.log)
    log_dir = os.environ.get("ProgramData", r"C:\\ProgramData")
    log_path = str(Path(log_dir) / PROJECT_NAME / "manager.log")
    script = f"""
param(
  [int]$PidToWait,
  [string]$Src,
  [string]$Dst,
  [string]$Args
)

$ErrorActionPreference = 'Stop'

$LogPath = '{_ps_escape_single(log_path)}'
function Log([string]$m) {{
  try {{
    $ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    Add-Content -Path $LogPath -Value "[$ts] $m"
  }} catch {{ }}
}}

Log "Updater: waiting for PID $PidToWait to exit"

# wait for current process to exit
try {{
  while (Get-Process -Id $PidToWait -ErrorAction SilentlyContinue) {{
    Start-Sleep -Milliseconds 500
  }}
}} catch {{}}

# ensure destination dir
$dstDir = Split-Path -Parent $Dst
New-Item -ItemType Directory -Force -Path $dstDir | Out-Null

# copy with retry (exe can remain locked briefly)
$copied = $false
for ($i=0; $i -lt 60; $i++) {{
  try {{
    Copy-Item -Force -Path $Src -Destination $Dst
    $copied = $true
    break
  }} catch {{
    Start-Sleep -Milliseconds 500
  }}
}}

if (-not $copied) {{
  throw "Failed to copy updated executable to $Dst"
}}

Log "Updater: copied updated executable to $Dst"

try {{
  if ([string]::IsNullOrWhiteSpace($Args)) {{
    $p = Start-Process -FilePath $Dst -PassThru
  }} else {{
    $p = Start-Process -FilePath $Dst -ArgumentList $Args -PassThru
  }}
  if ($p -and $p.Id) {{
    Log "Updater: started new manager process PID $($p.Id)"
  }} else {{
    Log "Updater: Start-Process returned no PID (unknown)"
  }}
}} catch {{
  Log "Updater: failed to start new manager: $($_.Exception.Message)"
  # Fallback: try cmd start
  try {{
    cmd /c start "" "$Dst" $Args | Out-Null
    Log "Updater: fallback cmd start invoked"
  }} catch {{ }}
  throw
}}
"""
    MANAGER_UPDATE_SCRIPT.write_text(script, encoding="utf-8")
    return MANAGER_UPDATE_SCRIPT


def start_update(
    release: dict,
    args: Optional[List[str]] = None,
    current_pid: Optional[int] = None,
    force: bool = False,
) -> None:
    """Stage release and trigger elevated replacement of installed manager."""
    args = args or []
    pid = int(current_pid or os.getpid())

    src = stage_download(release, force=force)
    dst = _installed_manager_path()

    _write_update_script(pid, src, dst, args)

    # Launch elevated PowerShell to run the script
    script = str(MANAGER_UPDATE_SCRIPT)
    ps = _powershell_exe()

    # Use ShellExecuteW runas for UAC prompt
    try:
        import ctypes

        args_str = " ".join(args)
        # Keep it simple: our args are typically things like --hidden.
        args_str = args_str.replace('"', '')

        params = (
            f"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" "
            f"-PidToWait {pid} -Src \"{src}\" -Dst \"{dst}\" "
            f"-Args \"{args_str}\""
        )
        rc = ctypes.windll.shell32.ShellExecuteW(None, "runas", ps, params, None, 0)
        if rc <= 32:
            raise RuntimeError(f"ShellExecuteW failed with code {rc}")
    except Exception as exc:
        log(f"Failed to start elevated update: {exc}")
        raise

    # After triggering update, exit current process so the script can replace the exe.
    log("Manager update started; exiting to allow replacement.")
    sys.exit(0)
