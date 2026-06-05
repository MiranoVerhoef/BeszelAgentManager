from __future__ import annotations
import os
import shutil
import io
import zipfile
import time
import re
import subprocess
import sys
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


LEGACY_AGENT_SERVICE_NAME = PROJECT_NAME


def _long_path(path: str) -> str:
    path = os.path.normpath(os.path.abspath(path))
    if os.name != "nt":
        return path
    try:
        import ctypes  # type: ignore[attr-defined]

        buf = ctypes.create_unicode_buffer(32768)
        rc = ctypes.windll.kernel32.GetLongPathNameW(str(path), buf, len(buf))  # type: ignore[attr-defined]
        if 0 < rc < len(buf):
            return buf.value
    except Exception:
        pass
    return path


def _format_cmd(cmd: list[str]) -> str:
    parts = []
    for item in cmd:
        text = str(item)
        if os.path.exists(text):
            text = _long_path(text)
        if " " in text:
            text = f'"{text}"'
        parts.append(text)
    return " ".join(parts)


def _clean_output(text: str) -> str:
    if not text:
        return ""
    if "\x00" in text:
        text = text.replace("\x00", "")
    return text.strip()


def _service_exists(name: str) -> bool:
    try:
        cp = run(['sc', 'query', name], check=False)
    except FileNotFoundError:
        return False
    txt = (cp.stdout or '') + (cp.stderr or '')
    return ('does not exist' not in txt)


def _resolve_service_name() -> str:
    if _service_exists(AGENT_SERVICE_NAME):
        return AGENT_SERVICE_NAME
    if LEGACY_AGENT_SERVICE_NAME and _service_exists(LEGACY_AGENT_SERVICE_NAME):
        return LEGACY_AGENT_SERVICE_NAME
    return AGENT_SERVICE_NAME


def _service_binary_path(name: str) -> str | None:
    cp = run(['sc', 'qc', name], check=False)
    if cp.returncode != 0:
        return None
    for line in _clean_output((cp.stdout or '') + (cp.stderr or '')).splitlines():
        if 'BINARY_PATH_NAME' not in line:
            continue
        _, _, value = line.partition(':')
        value = value.strip().strip('"')
        if value:
            return _long_path(value)
    return None


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


def _bundled_nssm() -> str | None:
    candidates = []
    exe_dir = os.path.dirname(os.path.abspath(sys.executable))
    package_dir = os.path.dirname(os.path.abspath(__file__))
    app_dir = os.path.normpath(os.path.join(package_dir, ".."))
    install_dir = os.path.normpath(os.path.join(app_dir, ".."))
    candidates.extend(
        [
            os.path.join(install_dir, "nssm.exe"),
            os.path.join(os.path.dirname(exe_dir), "nssm.exe"),
        ]
    )

    seen: set[str] = set()
    for candidate in candidates:
        path = _long_path(candidate)
        key = os.path.normcase(path)
        if key in seen:
            continue
        seen.add(key)
        if os.path.exists(path):
            if _looks_like_nssm_binary(path):
                log(f"Using bundled NSSM at {path}")
                return path
            log(f"Ignoring unusable bundled NSSM at {path}")
    return None


def _looks_like_nssm_binary(path: str) -> bool:
    try:
        if os.path.getsize(path) < 200_000:
            return False
        with open(path, "rb") as fh:
            return fh.read(2) == b"MZ"
    except OSError:
        return False


def _find_nssm() -> str:
    override = os.environ.get('NSSM_PATH')
    if override and shutil.which(override):
        return override
    bundled = _bundled_nssm()
    if bundled:
        return bundled
    nssm = shutil.which('nssm')
    if nssm:
        return nssm
    return _download_nssm()


def _configure_agent_logging(nssm: str) -> None:
    try:
        AGENT_LOG_DIR.mkdir(parents=True, exist_ok=True)
    except Exception as exc:
        log(f"Failed to create agent log dir {AGENT_LOG_DIR}: {exc}")
        return

    run([nssm, 'set', AGENT_SERVICE_NAME, 'AppStdout', str(AGENT_LOG_CURRENT_PATH)], check=False)
    run([nssm, 'set', AGENT_SERVICE_NAME, 'AppStderr', str(AGENT_LOG_CURRENT_PATH)], check=False)

    run([nssm, 'set', AGENT_SERVICE_NAME, 'AppRotation', '1'], check=False)
    run([nssm, 'set', AGENT_SERVICE_NAME, 'AppRotateOnline', '1'], check=False)
    run([nssm, 'set', AGENT_SERVICE_NAME, 'AppTimestampLog', '1'], check=False)


def _run_service_command(cmd: list[str], description: str) -> subprocess.CompletedProcess:
    cp = run(cmd, check=False)
    if cp.returncode != 0:
        stdout = _clean_output(cp.stdout or '')
        stderr = _clean_output(cp.stderr or '')
        details = [f"{description} failed ({cp.returncode}).", f"Command: {_format_cmd(cmd)}"]
        if stdout:
            details.append(f"Output: {stdout}")
        if stderr:
            details.append(f"Error: {stderr}")
        raise ServiceError(
            "\n".join(details)
        )
    return cp


def _require_service_exists() -> None:
    state = get_service_status()
    if state == 'NOT FOUND':
        raise ServiceError(
            f"Service '{AGENT_SERVICE_NAME}' was not created. "
            "Check the NSSM install output above and verify the app is running as administrator."
        )


def _ensure_service_uses_nssm_path(nssm: str) -> None:
    current = _service_binary_path(AGENT_SERVICE_NAME)
    if not current:
        return
    desired = _long_path(nssm)
    if os.path.normcase(current) == os.path.normcase(desired):
        return
    log(f"Service {AGENT_SERVICE_NAME} uses NSSM at {current}; recreating it with {desired}.")
    run([desired, 'stop', AGENT_SERVICE_NAME], check=False)
    run([desired, 'remove', AGENT_SERVICE_NAME, 'confirm'], check=False)
    run(['sc', 'delete', AGENT_SERVICE_NAME], check=False)
    deadline = time.time() + 10
    while _service_exists(AGENT_SERVICE_NAME) and time.time() < deadline:
        time.sleep(0.5)
    if _service_exists(AGENT_SERVICE_NAME):
        _run_service_command(
            ['sc', 'config', AGENT_SERVICE_NAME, 'binPath=', f'"{desired}"'],
            "Update service NSSM executable path",
        )


def create_or_update_service(env_vars: Dict[str, str]) -> None:
    if not AGENT_EXE_PATH.exists():
        raise ServiceError(f'Agent executable not found: {AGENT_EXE_PATH}')
    nssm = _find_nssm()

    if LEGACY_AGENT_SERVICE_NAME != AGENT_SERVICE_NAME and _service_exists(LEGACY_AGENT_SERVICE_NAME):
        run([nssm, 'stop', LEGACY_AGENT_SERVICE_NAME], check=False)
        run([nssm, 'remove', LEGACY_AGENT_SERVICE_NAME, 'confirm'], check=False)
        run(['sc', 'delete', LEGACY_AGENT_SERVICE_NAME], check=False)

    AGENT_DIR.mkdir(parents=True, exist_ok=True)
    if _service_exists(AGENT_SERVICE_NAME):
        _ensure_service_uses_nssm_path(nssm)

    if not _service_exists(AGENT_SERVICE_NAME):
        _run_service_command([nssm, 'install', AGENT_SERVICE_NAME, str(AGENT_EXE_PATH)], "NSSM service install")
    else:
        log(f"Service {AGENT_SERVICE_NAME} already exists; updating configuration.")
    _require_service_exists()

    _run_service_command([nssm, 'set', AGENT_SERVICE_NAME, 'DisplayName', AGENT_DISPLAY_NAME], "Set service display name")
    _run_service_command(
        [
            nssm,
            'set',
            AGENT_SERVICE_NAME,
            'Description',
            f"Beszel agent service managed by {PROJECT_NAME}. Configure and update via {PROJECT_NAME}.",
        ],
        "Set service description",
    )
    _run_service_command([nssm, 'set', AGENT_SERVICE_NAME, 'AppDirectory', str(AGENT_DIR)], "Set service app directory")

    _run_service_command([nssm, 'set', AGENT_SERVICE_NAME, 'AppStopMethodConsole', '1500'], "Set service console stop timeout")
    _run_service_command([nssm, 'set', AGENT_SERVICE_NAME, 'AppStopMethodWindow', '1500'], "Set service window stop timeout")
    _run_service_command([nssm, 'set', AGENT_SERVICE_NAME, 'AppStopMethodThreads', '1500'], "Set service thread stop timeout")

    _configure_agent_logging(nssm)
    env_pairs = [f"{k}={v}" for k, v in env_vars.items() if v]

    run([nssm, 'reset', AGENT_SERVICE_NAME, 'AppEnvironment'], check=False)
    run([nssm, 'reset', AGENT_SERVICE_NAME, 'AppEnvironmentExtra'], check=False)

    if env_pairs:
        _run_service_command([nssm, 'set', AGENT_SERVICE_NAME, 'AppEnvironmentExtra', *env_pairs], "Set service environment")
    _run_service_command([nssm, 'set', AGENT_SERVICE_NAME, 'Start', 'SERVICE_AUTO_START'], "Set service start mode")
    _run_service_command([nssm, 'set', AGENT_SERVICE_NAME, 'AppRestartDelay', '5000'], "Set service restart delay")

    restart_service(timeout_seconds=15)


def rotate_service_logs() -> None:
    try:
        nssm = _find_nssm()
    except Exception as exc:
        log(f"Unable to find NSSM for log rotation: {exc}")
        return
    svc = _resolve_service_name()
    run([nssm, 'rotate', svc], check=False)
    log('Requested agent log rotation.')


def get_service_status() -> str:
    try:
        cp = run(['sc', 'query', _resolve_service_name()], check=False)
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
    svc = _resolve_service_name()
    state, _ = _query_service_state_and_pid()
    if state == 'RUNNING':
        return
    if state == 'NOT FOUND':
        log('Service start requested but service was not found.')
        return
    run(['sc', 'start', svc], check=False)
    log('Service start requested.')


def _query_service_state_and_pid() -> tuple[str, int]:
    try:
        cp = run(['sc', 'queryex', _resolve_service_name()], check=False)
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
    forced = False
    svc = _resolve_service_name()
    state, _ = _query_service_state_and_pid()
    if state == 'STOPPED':
        return False
    if state == 'NOT FOUND':
        log('Service stop requested but service was not found.')
        return False

    run(['sc', 'stop', svc], check=False)
    log('Service stop requested.')

    try:
        time.sleep(1)
        state2, _ = _query_service_state_and_pid()
        if state2 == 'STOP_PENDING':
            nssm = _find_nssm()
            run([nssm, 'stop', svc], check=False)
    except Exception:
        pass

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
    if LEGACY_AGENT_SERVICE_NAME != AGENT_SERVICE_NAME:
        run([nssm, 'remove', LEGACY_AGENT_SERVICE_NAME, 'confirm'], check=False)
        run(['sc', 'delete', LEGACY_AGENT_SERVICE_NAME], check=False)
    run(['sc', 'delete', AGENT_SERVICE_NAME], check=False)
    log('Service removed.')


def get_service_diagnostics() -> str:
    lines: list[str] = []
    try:
        cp = run(['sc', 'queryex', _resolve_service_name()], check=False)
        lines.append('=== sc queryex ===')
        lines.append(_clean_output(cp.stdout or cp.stderr or ''))
    except Exception as exc:
        lines.append(f'=== sc queryex failed: {exc} ===')

    try:
        nssm = _find_nssm()
        lines.append('=== nssm get (selected) ===')
        svc = _resolve_service_name()
        for key in ['DisplayName', 'Description', 'Application', 'AppDirectory', 'AppStdout', 'AppStderr', 'AppEnvironment', 'AppEnvironmentExtra', 'Start', 'AppRestartDelay', 'AppStopMethodConsole', 'AppStopMethodWindow', 'AppStopMethodThreads']:
            cp = run([nssm, 'get', svc, key], check=False)
            val = _clean_output(cp.stdout or cp.stderr or '')
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


def open_nssm_edit() -> None:
    nssm = _find_nssm()
    svc = _resolve_service_name()
    try:
        # NSSM is a console-subsystem binary, but the "edit" action opens a GUI.
        # Using CREATE_NO_WINDOW prevents an extra console window from appearing behind the GUI.
        creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        subprocess.Popen([str(nssm), "edit", str(svc)], close_fds=True, creationflags=creationflags)
    except Exception as exc:
        raise ServiceError(f"Failed to open NSSM editor: {exc}") from exc


def remove_firewall_rule() -> None:
    run([
        'netsh', 'advfirewall', 'firewall', 'delete', 'rule',
        f'name={FIREWALL_RULE_NAME}',
    ], check=False)
    log('Firewall rule removed (if present).')
