from __future__ import annotations
import io
import zipfile
import re
import subprocess
import os
import requests
from .constants import (
    AGENT_DIR,
    AGENT_EXE_PATH,
    AGENT_DOWNLOAD_URL,
    DEFAULT_LISTEN_PORT,
)
from .config import AgentConfig
from .util import log
from .windows_service import (
    create_or_update_service,
    ensure_firewall_rule,
    restart_service,
    get_service_status,
)
from .scheduler import create_or_update_update_task, delete_update_task


def download_and_install_agent() -> None:
    AGENT_DIR.mkdir(parents=True, exist_ok=True)
    log(f"Downloading agent from {AGENT_DOWNLOAD_URL}")
    resp = requests.get(AGENT_DOWNLOAD_URL, timeout=60)
    if resp.status_code != 200:
        raise RuntimeError(f"Download failed (HTTP {resp.status_code})")
    with zipfile.ZipFile(io.BytesIO(resp.content)) as zf:
        exe_name = None
        for name in zf.namelist():
            if name.lower().endswith("beszel-agent.exe"):
                exe_name = name
                break
        if not exe_name:
            raise RuntimeError("Agent zip does not contain beszel-agent.exe")
        with zf.open(exe_name) as src, AGENT_EXE_PATH.open("wb") as dst:
            dst.write(src.read())
    try:
        AGENT_EXE_PATH.chmod(0o755)
    except PermissionError:
        pass
    log(f"Installed agent to {AGENT_EXE_PATH}")


def build_env_from_config(cfg: AgentConfig):
    env = {
        "KEY": cfg.key.strip(),
        "TOKEN": cfg.token.strip(),
        "HUB_URL": cfg.hub_url.strip(),
        "LISTEN": str(cfg.listen or DEFAULT_LISTEN_PORT),
    }

    def add(name: str, value: str):
        if value:
            env[name] = value

    add("DATA_DIR", cfg.data_dir.strip())
    add("DOCKER_HOST", cfg.docker_host.strip())
    add("EXCLUDE_CONTAINERS", cfg.exclude_containers.strip())
    add("EXCLUDE_SMART", cfg.exclude_smart.strip())
    add("EXTRA_FILESYSTEMS", cfg.extra_filesystems.strip())
    add("FILESYSTEM", cfg.filesystem.strip())
    add("INTEL_GPU_DEVICE", cfg.intel_gpu_device.strip())
    add("KEY_FILE", cfg.key_file.strip())
    add("TOKEN_FILE", cfg.token_file.strip())
    add("LHM", cfg.lhm.strip())
    add("LOG_LEVEL", cfg.log_level.strip())
    add("MEM_CALC", cfg.mem_calc.strip())
    add("NETWORK", cfg.network.strip())
    add("NICS", cfg.nics.strip())
    add("SENSORS", cfg.sensors.strip())
    add("PRIMARY_SENSOR", cfg.primary_sensor.strip())
    add("SYS_SENSORS", cfg.sys_sensors.strip())
    add("SERVICE_PATTERNS", cfg.service_patterns.strip())
    add("SMART_DEVICES", cfg.smart_devices.strip())
    add("SYSTEM_NAME", cfg.system_name.strip())
    add("SKIP_GPU", cfg.skip_gpu.strip())
    return env


def install_or_update_agent_and_service(cfg: AgentConfig) -> None:
    if not cfg.key.strip():
        raise ValueError("KEY is required. Paste the public key from the Beszel hub.")
    download_and_install_agent()
    env = build_env_from_config(cfg)
    create_or_update_service(env)
    ensure_firewall_rule(cfg.listen or DEFAULT_LISTEN_PORT)
    if cfg.auto_update_enabled:
        create_or_update_update_task(cfg.update_interval_days or 1)
    else:
        delete_update_task()


def apply_configuration_only(cfg: AgentConfig) -> None:
    env = build_env_from_config(cfg)
    create_or_update_service(env)
    ensure_firewall_rule(cfg.listen or DEFAULT_LISTEN_PORT)
    if cfg.auto_update_enabled:
        create_or_update_update_task(cfg.update_interval_days or 1)
    else:
        delete_update_task()


def _parse_download_version() -> str:
    m = re.search(r"/v(\d+\.\d+\.\d+)/", AGENT_DOWNLOAD_URL)
    if m:
        return m.group(1)
    return ""


def get_agent_version() -> str:
    if not AGENT_EXE_PATH.exists():
        return "Not installed"
    try:
        creationflags = 0
        if os.name == "nt":
            creationflags = subprocess.CREATE_NO_WINDOW
        cp = subprocess.run(
            [str(AGENT_EXE_PATH), "-h"],
            capture_output=True,
            text=True,
            timeout=5,
            creationflags=creationflags,
        )
        output = (cp.stdout or "") + "\n" + (cp.stderr or "")
        m = re.search(r"\b(\d+\.\d+\.\d+)\b", output)
        if m:
            return m.group(1)
    except Exception as exc:
        log(f"Failed to query agent version: {exc}")
    fallback = _parse_download_version()
    return fallback or "Unknown"


def update_agent_only() -> None:
    download_and_install_agent()
    status = get_service_status()
    if status not in ("NOT_FOUND", "NOT FOUND"):
        restart_service()
        log("Agent binary updated and service restart requested.")


def check_hub_status(hub_url: str) -> str:
    """Best-effort hub connectivity check.

    Returns a short status string:
    - 'Not configured' if hub_url is empty
    - 'Reachable' if an HTTP response (< 500) is received
    - 'Unreachable' or 'HTTP <code>' on error
    """
    url = (hub_url or "").strip()
    if not url:
        return "Not configured"
    if not url.startswith("http://") and not url.startswith("https://"):
        url = "https://" + url
    try:
        # For self-hosted hubs with custom / self-signed certs, be lenient on TLS verification.
        resp = requests.get(url, timeout=3, verify=False)
        if resp.status_code < 500:
            return "Reachable"
        return f"HTTP {resp.status_code}"
    except Exception as exc:
        log(f"Hub status check failed for {url}: {exc}")
        return "Unreachable"
