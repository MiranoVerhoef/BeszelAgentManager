from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import urllib.request
import ssl
import zipfile
from pathlib import Path
from typing import Optional, Tuple

from .config import AgentConfig
from .constants import AGENT_DIR, DATA_DIR, PROJECT_NAME, AGENT_STAGED_EXE_PATH
from .scheduler import delete_update_task, ensure_agent_log_rotate_task
from .util import log, github_headers
from .windows_service import create_or_update_service, get_service_status, stop_service

from .scheduler import ensure_update_task, ensure_periodic_restart_task, delete_periodic_restart_task


AGENT_EXE_NAME = "beszel-agent.exe"
AGENT_ZIP_PATTERN = "beszel-agent_windows_amd64.zip"
GITHUB_REPO = "henrygd/beszel"

if os.name == "nt" and hasattr(subprocess, "CREATE_NO_WINDOW"):
    CREATE_NO_WINDOW = subprocess.CREATE_NO_WINDOW
else:
    CREATE_NO_WINDOW = 0


def _powershell_exe() -> str:
    return os.path.join(
        os.environ.get("SystemRoot", r"C:\\Windows"),
        "System32",
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe",
    )


def _ps_escape_single(s: str) -> str:
    return s.replace("'", "''")


def _ps_run(command: str, timeout: int = 60) -> subprocess.CompletedProcess:
    command = "try{[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12}catch{}; " + command
    ps = _powershell_exe()
    cmd = [ps, "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command]
    kwargs = {
        "capture_output": True,
        "text": True,
        "shell": False,
        "timeout": timeout,
    }
    if CREATE_NO_WINDOW:
        kwargs["creationflags"] = CREATE_NO_WINDOW
    return subprocess.run(cmd, **kwargs)



def _normalize_version(ver: str | None) -> str | None:
    if not ver:
        return None

    ver = ver.strip()

    if ver.lower().startswith("v"):
        ver = ver[1:].strip()

    m = re.search(r"\d+\.\d+\.\d+", ver)
    if m:
        return m.group(0)

    return ver or None


def _version_tuple(v: str) -> tuple[int, int, int]:
    parts = (str(v).split(".") + ["0", "0", "0"])[:3]
    try:
        return int(parts[0]), int(parts[1]), int(parts[2])
    except Exception:
        return 0, 0, 0



def _fetch_latest_agent_release() -> Tuple[Optional[str], Optional[str]]:
    url = f"https://api.github.com/repos/{GITHUB_REPO}/releases/latest"
    headers = github_headers()
    auth_used = "Authorization" in headers
    if auth_used:
        log("GitHub Auth: using token for GitHub lookup")
    try:
        req = urllib.request.Request(url, headers=headers)
        ctx = ssl.create_default_context()
        with urllib.request.urlopen(req, timeout=8, context=ctx) as resp:
            data = json.loads(resp.read().decode("utf-8", "replace"))

            if auth_used:
                log("GitHub Auth: token lookup succeeded")

        tag = str(data.get("tag_name") or "").strip()
        version = _normalize_version(tag)
        if not version:
            log("GitHub latest release: could not normalize tag_name")
            return None, None

        body = data.get("body") or ""
        log(f"Latest Beszel agent release from GitHub: tag={tag}, version={version}")
        return version, body
    except ssl.SSLCertVerificationError as exc:
        log(f"Failed to fetch latest Beszel release from GitHub (SSL verify): {exc}")
    except Exception as exc:
        log(f"Failed to fetch latest Beszel release from GitHub: {exc}")

    try:
        parts = [f"'{_ps_escape_single(k)}'='{_ps_escape_single(v)}'" for k, v in headers.items()]
        hdr = "@{" + ";".join(parts) + "}"
        u = _ps_escape_single(url)
        ps_cmd = f"$h={hdr}; (Invoke-WebRequest -UseBasicParsing -Headers $h -Uri '{u}').Content"
        cp = _ps_run(ps_cmd, timeout=20)
        if cp.returncode != 0:
            raise RuntimeError(cp.stderr.strip())
        data = json.loads((cp.stdout or "").encode("utf-8", "ignore").decode("utf-8", "replace"))

        tag = str(data.get("tag_name") or "").strip()
        version = _normalize_version(tag)
        if not version:
            log("GitHub latest release (PowerShell): could not normalize tag_name")
            return None, None
        body = data.get("body") or ""
        log(f"Latest Beszel agent release from GitHub (PowerShell): tag={tag}, version={version}")
        return version, body
    except Exception as exc:
        log(f"PowerShell fallback for GitHub latest release failed: {exc}")
        return None, None


def fetch_agent_stable_releases(limit: int = 50) -> list[dict]:
    url = f"https://api.github.com/repos/{GITHUB_REPO}/releases?per_page={limit}"
    headers = github_headers()
    def _extract_release_list(data) -> list[dict]:
        out: list[dict] = []
        for r in data if isinstance(data, list) else []:
            if r.get("draft") or r.get("prerelease"):
                continue
            tag = str(r.get("tag_name") or "").strip()
            version = _normalize_version(tag)
            if not version:
                continue
            asset_url = None
            for a in (r.get("assets") or []):
                name = str(a.get("name") or "").lower()
                if name == "beszel-agent_windows_amd64.zip":
                    asset_url = a.get("browser_download_url")
                    break
            if not asset_url:
                continue
            out.append(
                {
                    "version": version,
                    "tag": tag,
                    "body": str(r.get("body") or ""),
                    "download_url": asset_url,
                    "published_at": r.get("published_at"),
                }
            )
        out.sort(key=lambda x: _version_tuple(x["version"]), reverse=True)
        return out

    try:
        req = urllib.request.Request(url, headers=headers)
        ctx = ssl.create_default_context()
        with urllib.request.urlopen(req, timeout=12, context=ctx) as resp:
            data = json.loads(resp.read().decode("utf-8", "replace"))
        releases = _extract_release_list(data)
        log(f"Fetched {len(releases)} stable Beszel releases from GitHub")
        return releases
    except ssl.SSLCertVerificationError as exc:
        log(f"Failed to fetch Beszel releases from GitHub (SSL verify): {exc}")
    except Exception as exc:
        log(f"Failed to fetch Beszel releases from GitHub: {exc}")

    try:
        parts = [f"'{_ps_escape_single(k)}'='{_ps_escape_single(v)}'" for k, v in headers.items()]
        hdr = "@{" + ";".join(parts) + "}"
        u = _ps_escape_single(url)
        ps_cmd = f"$h={hdr}; (Invoke-WebRequest -UseBasicParsing -Headers $h -Uri '{u}').Content"
        cp = _ps_run(ps_cmd, timeout=25)
        if cp.returncode != 0:
            raise RuntimeError((cp.stderr or cp.stdout or "").strip())
        data = json.loads((cp.stdout or "").encode("utf-8", "ignore").decode("utf-8", "replace"))
        releases = _extract_release_list(data)
        log(f"Fetched {len(releases)} stable Beszel releases from GitHub (PowerShell)")
        return releases
    except Exception as exc:
        log(f"PowerShell fallback for Beszel releases failed: {exc}")
        return []


def _parse_download_version() -> str:
    latest, _body = _fetch_latest_agent_release()
    if latest:
        return latest

    fallback = "0.16.1"
    log(f"Falling back to hard-coded agent version {fallback}")
    return fallback



def _ensure_agent_dir() -> Path:
    AGENT_DIR.mkdir(parents=True, exist_ok=True)
    return AGENT_DIR


def _agent_exe_path() -> Path:
    return _ensure_agent_dir() / AGENT_EXE_NAME


def _try_apply_staged_agent_update() -> bool:
    try:
        exe_path = _agent_exe_path()
        staged = AGENT_STAGED_EXE_PATH
        if not staged.exists():
            return False

        try:
            stop_service(timeout_seconds=30)
        except Exception:
            pass

        try:
            if exe_path.exists():
                try:
                    os.chmod(exe_path, 0o666)
                    exe_path.unlink()
                except Exception:
                    pass
            shutil.move(str(staged), str(exe_path))
            log(f"Applied staged agent update: {staged} -> {exe_path}")
            return True
        except Exception as exc:
            log(f"Staged agent update exists but could not be applied yet: {exc}")
            return False
    except Exception:
        return False


def _schedule_agent_replace_on_reboot(src: Path, dst: Path) -> None:
    if os.name != "nt":
        return
    try:
        import ctypes  # type: ignore

        MOVEFILE_REPLACE_EXISTING = 0x1
        MOVEFILE_DELAY_UNTIL_REBOOT = 0x4
        flags = MOVEFILE_REPLACE_EXISTING | MOVEFILE_DELAY_UNTIL_REBOOT
        ctypes.windll.kernel32.MoveFileExW(str(src), str(dst), flags)  # type: ignore[attr-defined]
        log(f"Scheduled agent replace on reboot: {src} -> {dst}")
    except Exception:
        pass


def _download_and_extract_agent(version: str, changelog: Optional[str] = None) -> None:
    agent_dir = _ensure_agent_dir()
    exe_path = _agent_exe_path()

    tmp_dir = DATA_DIR / "tmp_agent"
    zip_path = tmp_dir / "beszel-agent.zip"
    extract_dir = tmp_dir / "extract"

    try:
        if extract_dir.exists():
            shutil.rmtree(extract_dir, ignore_errors=True)
        tmp_dir.mkdir(parents=True, exist_ok=True)
        extract_dir.mkdir(parents=True, exist_ok=True)
    except Exception as exc:
        log(f"Warning: could not create temp extract dir {tmp_dir}: {exc}. Falling back to agent dir.")
        tmp_dir = agent_dir
        zip_path = agent_dir / "beszel-agent.zip"
        extract_dir = agent_dir

    try:
        stop_service(timeout_seconds=30)
    except Exception:
        pass

    if exe_path.exists():
        try:
            os.chmod(exe_path, 0o666)
            exe_path.unlink()
        except PermissionError as exc:
            log(f"Agent executable is in use; will stage update instead: {exc}")
        except Exception as exc:
            log(f"Warning: could not remove existing agent exe before update: {exc}")

    url = (
        f"https://github.com/{GITHUB_REPO}/releases/download/"
        f"v{version}/{AGENT_ZIP_PATTERN}"
    )

    log(f"Downloading agent from {url}")
    try:
        req = urllib.request.Request(url, headers={"User-Agent": PROJECT_NAME})
        ctx = ssl.create_default_context()
        with urllib.request.urlopen(req, timeout=30, context=ctx) as resp, zip_path.open("wb") as f:
            shutil.copyfileobj(resp, f)
    except ssl.SSLCertVerificationError as exc:
        log(f"Download failed due to SSL verify error: {exc}. Retrying via PowerShell...")
        u = _ps_escape_single(url)
        outp = _ps_escape_single(str(zip_path))
        ps_cmd = f"Invoke-WebRequest -UseBasicParsing -Uri '{u}' -OutFile '{outp}'"
        cp = _ps_run(ps_cmd, timeout=60)
        if cp.returncode != 0:
            raise RuntimeError(f"Download failed: {cp.stderr.strip()}") from exc
    except Exception as exc:
        log(f"Download failed: {exc}. Retrying via PowerShell...")
        u = _ps_escape_single(url)
        outp = _ps_escape_single(str(zip_path))
        ps_cmd = f"Invoke-WebRequest -UseBasicParsing -Uri '{u}' -OutFile '{outp}'"
        cp = _ps_run(ps_cmd, timeout=60)
        if cp.returncode != 0:
            raise RuntimeError(f"Download failed: {cp.stderr.strip()}") from exc

    log("Extracting agent zip")
    try:
        with zipfile.ZipFile(zip_path, "r") as zf:
            zf.extractall(extract_dir)
    except PermissionError as exc:
        raise RuntimeError(
            f"Failed to extract agent zip because {exe_path} is in use.\n"
            "Please stop the Beszel agent service and try again."
        ) from exc
    except Exception as exc:
        raise RuntimeError(f"Failed to extract agent zip: {exc}") from exc
    finally:
        try:
            if zip_path.exists():
                zip_path.unlink()
        except Exception as exc:
            log(f"Failed to remove agent zip: {exc}")

    extracted_exe: Optional[Path] = None
    try:
        for p in extract_dir.rglob(AGENT_EXE_NAME):
            extracted_exe = p
            break
    except Exception:
        extracted_exe = None

    if not extracted_exe or not extracted_exe.exists():
        raise RuntimeError(f"Agent binary not found after extract: expected {AGENT_EXE_NAME}")

    try:
        if extracted_exe.resolve() != exe_path.resolve():
            exe_path.parent.mkdir(parents=True, exist_ok=True)

            try:
                if exe_path.exists():
                    try:
                        os.chmod(exe_path, 0o666)
                        exe_path.unlink()
                    except Exception:
                        pass
                shutil.move(str(extracted_exe), str(exe_path))
            except Exception as exc:
                staged = AGENT_STAGED_EXE_PATH
                try:
                    if staged.exists():
                        staged.unlink()
                except Exception:
                    pass
                shutil.move(str(extracted_exe), str(staged))
                _schedule_agent_replace_on_reboot(staged, exe_path)
                log(
                    f"Agent binary replacement is blocked ({exc}). Staged update at {staged}. "
                    "It will be applied automatically when possible (or after reboot)."
                )
    finally:
        try:
            if tmp_dir != agent_dir and tmp_dir.exists():
                shutil.rmtree(tmp_dir, ignore_errors=True)
        except Exception:
            pass

    if not exe_path.exists() and not AGENT_STAGED_EXE_PATH.exists():
        raise RuntimeError(f"Agent binary not found after move: {exe_path}")

    log_msg = f"Installed/updated Beszel agent to version {version}."
    if changelog:
        log_msg += "\nWhat's changed (from GitHub release):\n"
        lines = [ln.rstrip() for ln in changelog.splitlines()]
        while lines and not lines[0]:
            lines.pop(0)
        while lines and not lines[-1]:
            lines.pop()
        max_lines = 40
        if len(lines) > max_lines:
            lines = lines[:max_lines] + [
                "...",
                "(truncated; see GitHub release for full notes)",
            ]
        log_msg += "\n" + "\n".join(lines)
    log(log_msg)


def _build_env_from_config(cfg: AgentConfig, *, include_process_env: bool = False) -> dict:
    env = os.environ.copy() if include_process_env else {}

    def set_if(value: object, key: str):
        if value is None:
            return
        if isinstance(value, bool):
            v = "1" if value else ""
        else:
            v = str(value).strip()
        if v != "":
            env[key] = v

    set_if(cfg.key, "KEY")
    set_if(cfg.token, "TOKEN")
    set_if(cfg.hub_url, "HUB_URL")
    set_if(cfg.listen, "LISTEN")

    mapping: dict[str, str] = {
        "DATA_DIR": "data_dir",
        "DOCKER_HOST": "docker_host",
        "EXCLUDE_CONTAINERS": "exclude_containers",
        "EXCLUDE_SMART": "exclude_smart",
        "EXTRA_FILESYSTEMS": "extra_filesystems",
        "FILESYSTEM": "filesystem",
        "INTEL_GPU_DEVICE": "intel_gpu_device",
        "NVML": "nvml",
        "KEY_FILE": "key_file",
        "TOKEN_FILE": "token_file",
        "LHM": "lhm",
        "LOG_LEVEL": "log_level",
        "MEM_CALC": "mem_calc",
        "NETWORK": "network",
        "NICS": "nics",
        "SENSORS": "sensors",
        "PRIMARY_SENSOR": "primary_sensor",
        "SYS_SENSORS": "sys_sensors",
        "SERVICE_PATTERNS": "service_patterns",
        "SMART_DEVICES": "smart_devices",
        "SMART_INTERVAL": "smart_interval",
        "SYSTEM_NAME": "system_name",
        "SKIP_GPU": "skip_gpu",
        "DISK_USAGE_CACHE": "disk_usage_cache",
        "SKIP_SYSTEMD": "skip_systemd",
    }

    active = list(getattr(cfg, "env_active_names", []) or [])
    if not active:
        for env_name, attr in mapping.items():
            v = getattr(cfg, attr, "")
            if isinstance(v, str) and v.strip() != "":
                active.append(env_name)

    want_env = bool(getattr(cfg, "env_enabled", False))
    if not want_env and active:
        # If a user has active env rows configured, treat env as enabled.
        want_env = True

    if not want_env:
        return env

    for env_name in active:
        attr = mapping.get(env_name)
        if not attr:
            continue
        set_if(getattr(cfg, attr, ""), env_name)

    return env

def _run_agent_once(cfg: AgentConfig, args: list[str]) -> subprocess.CompletedProcess:
    exe_path = _agent_exe_path()
    if not exe_path.exists():
        raise RuntimeError("Agent binary does not exist, cannot run agent.")

    cmd = [str(exe_path)] + args
    env = _build_env_from_config(cfg, include_process_env=True)
    log(f"Running agent: {' '.join(cmd)}")
    return subprocess.run(
        cmd,
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        timeout=60,
        creationflags=CREATE_NO_WINDOW,
    )



def install_or_update_agent_and_service(cfg: AgentConfig) -> None:
    version, changelog = _fetch_latest_agent_release()
    if not version:
        version = _parse_download_version()
        changelog = None

    log(f"Starting agent install/update to version {version}")
    _download_and_extract_agent(version, changelog=changelog)

    env = _build_env_from_config(cfg, include_process_env=False)
    log("Configuring Windows service for Beszel agent")

    create_or_update_service(env)

    ensure_agent_log_rotate_task()

    if cfg.auto_update_enabled:
        log(
            f"Ensuring scheduled update task (interval {cfg.update_interval_days} days)"
        )
        ensure_update_task(cfg.update_interval_days)
    else:
        log("Auto update disabled, removing scheduled update task if present")
        delete_update_task()

    if getattr(cfg, "auto_restart_enabled", False):
        hours = int(getattr(cfg, "auto_restart_interval_hours", 24) or 24)
        log(f"Ensuring periodic restart task (every {hours} hour(s))")
        ensure_periodic_restart_task(hours)
    else:
        log("Periodic restart disabled, removing restart task if present")
        delete_periodic_restart_task()


def apply_configuration_only(cfg: AgentConfig) -> None:
    exe_path = _agent_exe_path()
    if not exe_path.exists():
        raise RuntimeError("Agent is not installed yet.")

    env = _build_env_from_config(cfg, include_process_env=False)
    log("Updating Windows service configuration for Beszel agent")

    create_or_update_service(env)

    ensure_agent_log_rotate_task()

    if cfg.auto_update_enabled:
        log(
            f"Ensuring scheduled update task (interval {cfg.update_interval_days} days)"
        )
        ensure_update_task(cfg.update_interval_days)
    else:
        log("Auto update disabled, removing scheduled update task if present")
        delete_update_task()

    if getattr(cfg, "auto_restart_enabled", False):
        hours = int(getattr(cfg, "auto_restart_interval_hours", 24) or 24)
        log(f"Ensuring periodic restart task (every {hours} hour(s))")
        ensure_periodic_restart_task(hours)
    else:
        log("Periodic restart disabled, removing restart task if present")
        delete_periodic_restart_task()


def update_agent_only(version: str | None = None, changelog: str | None = None) -> None:
    if not version:
        version, changelog = _fetch_latest_agent_release()
        if not version:
            version = _parse_download_version()
            changelog = None

    log(f"Updating agent binary to version {version}")
    _download_and_extract_agent(version, changelog=changelog)


def get_agent_version() -> str:
    exe_path = _agent_exe_path()
    if not exe_path.exists():
        return "Not installed"

    try:
        result = subprocess.run(
            [str(exe_path), "version"],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=10,
            creationflags=CREATE_NO_WINDOW,
        )
        raw = (result.stdout or "").strip() or (result.stderr or "").strip()
        if not raw:
            return "Unknown"

        norm = _normalize_version(raw)
        if not norm:
            log(f"Could not normalize agent version from output: {raw!r}")
            return "Unknown"

        return norm
    except Exception as exc:
        log(f"Failed to get agent version: {exc}")
        return "Unknown"


def check_hub_status(hub_url: str | None) -> str:
    if not hub_url:
        return "Not configured"
    return "Configured"


def get_current_service_status() -> str:
    return get_service_status()
