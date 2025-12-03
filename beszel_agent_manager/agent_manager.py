from __future__ import annotations

import os
import shutil
import subprocess
import zipfile
import json
import urllib.request
import re
from pathlib import Path
from typing import Optional, Tuple

from .constants import (
    AGENT_DIR,
    DATA_DIR,
    PROJECT_NAME,
    APP_VERSION,
)
from .util import log
from .windows_service import (
    create_or_update_service,
    get_service_status,
)

# Always import delete_update_task (it exists in your scheduler)
from .scheduler import delete_update_task

# Try to import ensure_update_task, but gracefully handle older scheduler.py
try:
    from .scheduler import ensure_update_task  # type: ignore[attr-defined]
except ImportError:  # pragma: no cover
    def ensure_update_task(*args, **kwargs):
        """
        Fallback stub if this version of scheduler.py does not provide
        ensure_update_task. Auto-update scheduling will simply be disabled,
        but the manager will not crash.
        """
        log(
            "Warning: ensure_update_task not found in beszel_agent_manager.scheduler; "
            "auto-update scheduling is disabled in this build."
        )


from .config import AgentConfig


AGENT_EXE_NAME = "beszel-agent.exe"
AGENT_ZIP_PATTERN = "beszel-agent_windows_amd64.zip"
GITHUB_REPO = "henrygd/beszel"


# ---------------------------------------------------------------------------
# Version helpers
# ---------------------------------------------------------------------------

def _normalize_version(ver: str | None) -> str | None:
    """
    Extract a clean semantic version like 0.17.0 from various strings:
    - 'v0.17.0'
    - 'beszel-agent version 0.17.0'
    - '0.17.0 (windows/amd64)'
    """
    if not ver:
        return None

    ver = ver.strip()

    # Strip leading 'v' if present (v0.17.0 -> 0.17.0)
    if ver.lower().startswith("v"):
        ver = ver[1:].strip()

    # Look for first x.y.z pattern
    m = re.search(r"\d+\.\d+\.\d+", ver)
    if m:
        return m.group(0)

    # Fallback: return whatever we have, stripped
    return ver or None


# ---------------------------------------------------------------------------
# GitHub helpers
# ---------------------------------------------------------------------------

def _fetch_latest_agent_release() -> Tuple[Optional[str], Optional[str]]:
    """
    Query GitHub for the latest Beszel release and return (version, body).

    version: normalized version like '0.17.0'
    body:    release body (changelog text)

    Returns (None, None) on error.
    """
    url = f"https://api.github.com/repos/{GITHUB_REPO}/releases/latest"
    try:
        req = urllib.request.Request(
            url,
            headers={
                "User-Agent": PROJECT_NAME,
                "Accept": "application/vnd.github+json",
            },
        )
        with urllib.request.urlopen(req, timeout=8) as resp:
            data = json.loads(resp.read().decode("utf-8", "replace"))

        tag = str(data.get("tag_name") or "").strip()
        version = _normalize_version(tag)
        if not version:
            log("GitHub latest release: could not normalize tag_name")
            return None, None

        body = data.get("body") or ""
        log(f"Latest Beszel agent release from GitHub: tag={tag}, version={version}")
        return version, body
    except Exception as exc:
        log(f"Failed to fetch latest Beszel release from GitHub: {exc}")
        return None, None


def _parse_download_version() -> str:
    """
    Decide which version of the Beszel agent to download.

    - First try GitHub "latest release" (primary path)
    - If that fails, fall back to a hard-coded version (current fallback: 0.16.1)
    """
    latest, _body = _fetch_latest_agent_release()
    if latest:
        return latest

    fallback = "0.16.1"
    log(f"Falling back to hard-coded agent version {fallback}")
    return fallback


# ---------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------

def _ensure_agent_dir() -> Path:
    AGENT_DIR.mkdir(parents=True, exist_ok=True)
    return AGENT_DIR


def _agent_exe_path() -> Path:
    return _ensure_agent_dir() / AGENT_EXE_NAME


def _download_and_extract_agent(version: str, changelog: Optional[str] = None) -> None:
    """
    Download the agent zip for the given version and extract the exe.

    Also logs the version + optional changelog ("What's changed") to manager.log.
    """
    agent_dir = _ensure_agent_dir()

    url = (
        f"https://github.com/{GITHUB_REPO}/releases/download/"
        f"v{version}/{AGENT_ZIP_PATTERN}"
    )
    zip_path = agent_dir / "beszel-agent.zip"

    log(f"Downloading agent from {url}")
    try:
        with urllib.request.urlopen(url, timeout=30) as resp, zip_path.open("wb") as f:
            shutil.copyfileobj(resp, f)
    except Exception as exc:
        raise RuntimeError(f"Download failed: {exc}") from exc

    log(f"Extracting agent zip to {agent_dir}")
    try:
        with zipfile.ZipFile(zip_path, "r") as zf:
            zf.extractall(agent_dir)
    except Exception as exc:
        raise RuntimeError(f"Failed to extract agent zip: {exc}") from exc
    finally:
        try:
            if zip_path.exists():
                zip_path.unlink()
        except Exception as exc:
            log(f"Failed to remove agent zip: {exc}")

    exe_path = _agent_exe_path()
    if not exe_path.exists():
        raise RuntimeError(f"Agent binary not found after extract: {exe_path}")

    log_msg = f"Installed/updated Beszel agent to version {version}."
    if changelog:
        log_msg += "\nWhat's changed (from GitHub release):\n"
        # Avoid huge logs – keep a reasonable number of lines.
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


def _build_env_from_config(cfg: AgentConfig) -> dict:
    """
    Build environment variables dict for the agent process from AgentConfig.
    """
    env = os.environ.copy()

    def set_if(value: str | None, key: str):
        if value:
            env[key] = value

    set_if(cfg.key, "KEY")
    set_if(cfg.token, "TOKEN")
    set_if(cfg.hub_url, "HUB_URL")

    set_if(cfg.data_dir, "DATA_DIR")
    set_if(cfg.docker_host, "DOCKER_HOST")
    set_if(cfg.exclude_containers, "EXCLUDE_CONTAINERS")
    set_if(cfg.exclude_smart, "EXCLUDE_SMART")
    set_if(cfg.extra_filesystems, "EXTRA_FILESYSTEMS")
    set_if(cfg.filesystem, "FILESYSTEM")
    set_if(cfg.intel_gpu_device, "INTEL_GPU_DEVICE")
    set_if(cfg.key_file, "KEY_FILE")
    set_if(cfg.token_file, "TOKEN_FILE")
    set_if(cfg.lhm, "LHM")
    set_if(cfg.log_level, "LOG_LEVEL")
    set_if(cfg.mem_calc, "MEM_CALC")
    set_if(cfg.network, "NETWORK")
    set_if(cfg.nics, "NICS")
    set_if(cfg.sensors, "SENSORS")
    set_if(cfg.primary_sensor, "PRIMARY_SENSOR")
    set_if(cfg.sys_sensors, "SYS_SENSORS")
    set_if(cfg.service_patterns, "SERVICE_PATTERNS")
    set_if(cfg.smart_devices, "SMART_DEVICES")
    set_if(cfg.system_name, "SYSTEM_NAME")
    set_if(cfg.skip_gpu, "SKIP_GPU")

    # Newer env vars (v0.17.0+); may not exist on older configs
    disk_usage_cache = getattr(cfg, "disk_usage_cache", None)
    skip_systemd = getattr(cfg, "skip_systemd", None)
    set_if(disk_usage_cache, "DISK_USAGE_CACHE")
    set_if(skip_systemd, "SKIP_SYSTEMD")

    if cfg.listen:
        env["LISTEN"] = str(cfg.listen)

    return env


def _run_agent_once(cfg: AgentConfig, args: list[str]) -> subprocess.CompletedProcess:
    exe_path = _agent_exe_path()
    if not exe_path.exists():
        raise RuntimeError("Agent binary does not exist, cannot run agent.")

    cmd = [str(exe_path)] + args
    env = _build_env_from_config(cfg)
    log(f"Running agent: {' '.join(cmd)}")
    return subprocess.run(
        cmd,
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        timeout=60,
    )


# ---------------------------------------------------------------------------
# Public API used by GUI
# ---------------------------------------------------------------------------

def install_or_update_agent_and_service(cfg: AgentConfig) -> None:
    """
    Install or update the agent binary and configure the Windows service + scheduler.
    """
    version, changelog = _fetch_latest_agent_release()
    if not version:
        # Fallback path – still works, but no changelog
        version = _parse_download_version()
        changelog = None

    log(f"Starting agent install/update to version {version}")
    _download_and_extract_agent(version, changelog=changelog)

    # Service + scheduled task
    exe_path = _agent_exe_path()
    env = _build_env_from_config(cfg)
    log("Configuring Windows service for Beszel agent")
    create_or_update_service(
        exe_path=str(exe_path),
        listen_port=cfg.listen,
        env=env,
    )

    if cfg.auto_update_enabled:
        log(
            f"Ensuring scheduled update task (interval {cfg.update_interval_days} days)"
        )
        ensure_update_task(cfg.update_interval_days)
    else:
        log("Auto update disabled, removing scheduled update task if present")
        delete_update_task()


def apply_configuration_only(cfg: AgentConfig) -> None:
    """
    Apply configuration/env/service settings without re-downloading the agent.
    """
    exe_path = _agent_exe_path()
    if not exe_path.exists():
        raise RuntimeError("Agent is not installed yet.")

    env = _build_env_from_config(cfg)
    log("Updating Windows service configuration for Beszel agent")
    create_or_update_service(
        exe_path=str(exe_path),
        listen_port=cfg.listen,
        env=env,
    )

    if cfg.auto_update_enabled:
        log(
            f"Ensuring scheduled update task (interval {cfg.update_interval_days} days)"
        )
        ensure_update_task(cfg.update_interval_days)
    else:
        log("Auto update disabled, removing scheduled update task if present")
        delete_update_task()


def update_agent_only() -> None:
    """
    Update only the agent binary in AGENT_DIR, without touching service/scheduler.

    This is used by the GUI "Update agent" and by any code paths that want
    a pure binary update.
    """
    version, changelog = _fetch_latest_agent_release()
    if not version:
        version = _parse_download_version()
        changelog = None

    log(f"Updating agent binary to version {version}")
    _download_and_extract_agent(version, changelog=changelog)


def get_agent_version() -> str:
    """
    Ask the agent itself for its version, if installed.
    Returns:
        - "Not installed"
        - "Unknown"
        - or a normalized version string like "0.17.0"
    """
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
    """
    Simple status string used by the GUI – whether the hub URL looks configured.
    (Detailed reachability/ping is done in the GUI itself.)
    """
    if not hub_url:
        return "Not configured"
    return "Configured"


# Convenience used by other modules (if any)

def get_current_service_status() -> str:
    return get_service_status()
