from __future__ import annotations
import os
import shutil
import io
import zipfile
import time
import re
from typing import Dict

import requests

from .constants import (
    PROJECT_NAME,
    AGENT_DIR,
    AGENT_EXE_PATH,
    AGENT_SERVICE_NAME,
    AGENT_DISPLAY_NAME,
    FIREWALL_RULE_NAME,
    NSSM_DOWNLOAD_URL,
    NSSM_DIR,
    NSSM_EXE_PATH,
    AGENT_LOG_DIR,
    AGENT_LOG_CURRENT_PATH,
)
from .util import run, log


class ServiceError(RuntimeError):
    pass


def _download_nssm() -> str:
    NSSM_DIR.mkdir(parents=True, exist_ok=True)
    if NSSM_EXE_PATH.exists():
        log(f"Using existing bundled NSSM at {NSSM_EXE_PATH}")
        return str(NSSM_EXE_PATH)

    log(f"Downloading NSSM from {NSSM_DOWNLOAD_URL}")
    resp = requests.get(NSSM_DOWNLOAD_URL, timeout=60)
    if resp.status_code != 200:
        raise ServiceError(f"Failed to download NSSM (HTTP {resp.status_code}) from {NSSM_DOWNLOAD_URL}")

    try:
        with zipfile.ZipFile(io.BytesIO(resp.content)) as zf:
            candidate = None
            for name in zf.namelist():
                lower = name.replace('\\', '/').lower()
                if lower.endswith('win64/nssm.exe'):
                    candidate = name
                    break
            if candidate is None:
                for name in zf.namelist():
                    if name.lower().endswith('nssm.exe'):
                        candidate = name
                        break
            if candidate is None:
                raise ServiceError('NSSM zip does not contain nssm.exe')
            with zf.open(candidate) as src, NSSM_EXE_PATH.open('wb') as dst:
                dst.write(src.read())
    except zipfile.BadZipFile as exc:
        raise ServiceError(f'Downloaded NSSM archive is invalid: {exc}') from exc

    try:
        NSSM_EXE_PATH.chmod(0o755)
    except PermissionError:
        pass

    log(f'Downloaded NSSM to {NSSM_EXE_PATH}')
    return str(NSSM_EXE_PATH)


def _find_nssm() -> str:
    override = os.environ.get('NSSM_PATH')
    if override and shutil.which(override):
        return override
    nssm = shutil.which('nssm')
    if nssm:
        return nssm
    return _download_nssm()


def _configure_agent_logging(nssm: str) -> None:
    """Configure NSSM stdout/stderr redirection for the agent service."""
    try:
        AGENT_LOG_DIR.mkdir(parents=True, exist_ok=True)
    except Exception as exc:
        log(f"Failed to create agent log dir {AGENT_LOG_DIR}: {exc}")
        return

    # Redirect both stdout and stderr to a single file.
    run([nssm, 'set', AGENT_SERVICE_NAME, 'AppStdout', str(AGENT_LOG_CURRENT_PATH)], check=False)
    run([nssm, 'set', AGENT_SERVICE_NAME, 'AppStderr', str(AGENT_LOG_CURRENT_PATH)], check=False)

    # Enable on-demand rotation (nssm rotate) and timestamp prefixing.
    run([nssm, 'set', AGENT_SERVICE_NAME, 'AppRotation', '1'], check=False)
    run([nssm, 'set', AGENT_SERVICE_NAME, 'AppRotateOnline', '1'], check=False)
    run([nssm, 'set', AGENT_SERVICE_NAME, 'AppTimestampLog', '1'], check=False)


def create_or_update_service(env_vars: Dict[str, str]) -> None:
    if not AGENT_EXE_PATH.exists():
        raise ServiceError(f'Agent executable not found: {AGENT_EXE_PATH}')
    nssm = _find_nssm()
    AGENT_DIR.mkdir(parents=True, exist_ok=True)
    run([nssm, 'install', AGENT_SERVICE_NAME, str(AGENT_EXE_PATH)], check=False)
    run([nssm, 'set', AGENT_SERVICE_NAME, 'DisplayName', AGENT_DISPLAY_NAME], check=False)
    # Show in services.msc that this service is owned/maintained by the manager.
    run(
        [
            nssm,
            'set',
            AGENT_SERVICE_NAME,
            'Description',
            f"Beszel agent service managed by {PROJECT_NAME}. Configure and update via {PROJECT_NAME}.",
        ],
        check=False,
    )
    run([nssm, 'set', AGENT_SERVICE_NAME, 'AppDirectory', str(AGENT_DIR)], check=False)
    _configure_agent_logging(nssm)
    env_pairs = [f"{k}={v}" for k, v in env_vars.items() if v]
    if not env_pairs:
        env_pairs = [""]
    run([nssm, 'set', AGENT_SERVICE_NAME, 'AppEnvironmentExtra', *env_pairs], check=False)
    run([nssm, 'set', AGENT_SERVICE_NAME, 'Start', 'SERVICE_AUTO_START'], check=False)
    run([nssm, 'set', AGENT_SERVICE_NAME, 'AppRestartDelay', '5000'], check=False)

    # Restart via SCM so we can detect/handle STOP_PENDING hangs.
    restart_service(timeout_seconds=15)


def rotate_service_logs() -> None:
    """Trigger on-demand rotation of the agent service stdout/stderr logs."""
    try:
        nssm = _find_nssm()
    except Exception as exc:
        log(f"Unable to find NSSM for log rotation: {exc}")
        return
    run([nssm, 'rotate', AGENT_SERVICE_NAME], check=False)
    log('Requested NSSM log rotation for agent service.')


def get_service_status() -> str:
    try:
        cp = run(['sc', 'query', AGENT_SERVICE_NAME], check=False)
    except FileNotFoundError:
        return 'NOT FOUND'
    txt = (cp.stdout or '') + (cp.stderr or '')
    if 'does not exist' in txt:
        return 'NOT FOUND'
    for line in txt.splitlines():
        line = line.strip()
        if line.startswith('STATE'):
            parts = line.split()
            if parts:
                return parts[-1]
    return 'UNKNOWN'


def start_service() -> None:
    run(['sc', 'start', AGENT_SERVICE_NAME], check=False)
    log('Service start requested.')


def _query_service_state_and_pid() -> tuple[str, int]:
    """Return (STATE, PID) using `sc queryex`."""
    try:
        cp = run(['sc', 'queryex', AGENT_SERVICE_NAME], check=False)
    except FileNotFoundError:
        return ('NOT FOUND', 0)

    txt = (cp.stdout or '') + (cp.stderr or '')
    if 'does not exist' in txt:
        return ('NOT FOUND', 0)

    state = 'UNKNOWN'
    pid = 0
    for line in txt.splitlines():
        s = line.strip()
        if s.startswith('STATE'):
            parts = s.split()
            if parts:
                state = parts[-1]
        elif s.startswith('PID'):
            m = re.search(r"PID\s*:\s*(\d+)", s)
            if m:
                try:
                    pid = int(m.group(1))
                except Exception:
                    pid = 0
    return (state, pid)


def _wait_for_state(target_state: str, timeout_seconds: int = 15) -> bool:
    deadline = time.time() + max(1, int(timeout_seconds))
    while time.time() < deadline:
        state, _ = _query_service_state_and_pid()
        if state == target_state:
            return True
        time.sleep(1)
    return False


def _force_kill_service_process() -> None:
    state, pid = _query_service_state_and_pid()
    if pid and pid > 0:
        log(f"Service is stuck in {state}; forcing kill of service PID {pid}.")
        run(['taskkill', '/PID', str(pid), '/T', '/F'], check=False)
        return

    log(f"Service is stuck in {state}; PID not available. Attempting to kill beszel-agent.exe.")
    run(['taskkill', '/IM', 'beszel-agent.exe', '/T', '/F'], check=False)


def stop_service(timeout_seconds: int = 15) -> bool:
    """Stop service; if it doesn't stop within timeout, force-kill it.

    Returns True if a force-kill was performed.
    """
    forced = False
    run(['sc', 'stop', AGENT_SERVICE_NAME], check=False)
    log('Service stop requested.')

    if _wait_for_state('STOPPED', timeout_seconds=timeout_seconds):
        return False

    forced = True
    _force_kill_service_process()
    if _wait_for_state('STOPPED', timeout_seconds=10):
        log('Service force-stopped successfully.')
    else:
        log('Service still not STOPPED after force kill.')
    return forced


def restart_service(timeout_seconds: int = 15) -> bool:
    """Restart service; force-kills it if stop/start gets stuck.

    Returns True if a force-kill was performed.
    """
    forced = stop_service(timeout_seconds=timeout_seconds)
    start_service()

    if _wait_for_state('RUNNING', timeout_seconds=timeout_seconds):
        log('Service restarted successfully.')
        return forced

    log('Service did not reach RUNNING in time; forcing kill and retrying start.')
    forced = True
    _force_kill_service_process()
    time.sleep(2)
    start_service()
    if _wait_for_state('RUNNING', timeout_seconds=timeout_seconds):
        log('Service restarted successfully after force kill.')
    else:
        state, _ = _query_service_state_and_pid()
        log(f'Service still not RUNNING after retry (state={state}).')
    return forced


def delete_service() -> None:
    nssm = _find_nssm()
    try:
        stop_service(timeout_seconds=15)
    except Exception:
        pass
    run([nssm, 'remove', AGENT_SERVICE_NAME, 'confirm'], check=False)
    log('Service removed via NSSM.')


def get_service_diagnostics() -> str:
    """Return a multi-line diagnostics dump for support bundles/UI."""
    lines: list[str] = []
    try:
        cp = run(['sc', 'queryex', AGENT_SERVICE_NAME], check=False)
        lines.append('=== sc queryex ===')
        lines.append((cp.stdout or cp.stderr or '').strip())
    except Exception as exc:
        lines.append(f'=== sc queryex failed: {exc} ===')

    try:
        nssm = _find_nssm()
        # Grab a few useful settings
        lines.append('=== nssm get (selected) ===')
        for key in ['DisplayName', 'AppPath', 'AppDirectory', 'AppStdout', 'AppStderr', 'AppEnvironmentExtra', 'Start', 'AppRestartDelay']:
            cp = run([nssm, 'get', AGENT_SERVICE_NAME, key], check=False)
            val = (cp.stdout or cp.stderr or '').strip()
            lines.append(f'{key}: {val}')
    except Exception as exc:
        lines.append(f'=== nssm get failed: {exc} ===')

    return "\n".join(lines).strip() + "\n"


def ensure_firewall_rule(port: int) -> None:
    run([
        'netsh', 'advfirewall', 'firewall', 'add', 'rule',
        f'name={FIREWALL_RULE_NAME}',
        'dir=in',
        'action=allow',
        'protocol=TCP',
        f'localport={port}',
    ], check=False)
    log(f'Firewall rule ensured for TCP {port}.')


def remove_firewall_rule() -> None:
    run([
        'netsh', 'advfirewall', 'firewall', 'delete', 'rule',
        f'name={FIREWALL_RULE_NAME}',
    ], check=False)
    log('Firewall rule removed (if present).')
