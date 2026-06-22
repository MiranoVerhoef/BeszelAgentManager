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
    if cp.returncode == 0:
        return True
    text = _clean_output((cp.stdout or '') + (cp.stderr or ''))
    if '1060' in text or 'does not exist' in text.lower():
        return False
    raise ServiceError(
        f"Unable to query service '{name}' ({cp.returncode})."
        + (f"\n{text}" if text else "")
    )


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


def _service_pid(name: str) -> int | None:
    cp = run(['sc', 'queryex', name], check=False)
    if cp.returncode != 0:
        return None
    for line in _clean_output((cp.stdout or '') + (cp.stderr or '')).splitlines():
        line = line.strip()
        if not line.startswith('PID'):
            continue
        _, _, value = line.partition(':')
        value = value.strip()
        if value.isdigit():
            pid = int(value)
            return pid if pid > 0 else None
    return None


def _wait_until_service_removed(name: str, timeout_seconds: int) -> bool:
    deadline = time.time() + timeout_seconds
    while _service_exists(name) and time.time() < deadline:
        time.sleep(0.5)
    return not _service_exists(name)


def _wait_until_service_exists(name: str, timeout_seconds: int) -> bool:
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        if _service_exists(name):
            return True
        time.sleep(0.5)
    return _service_exists(name)


def _kill_service_process_if_present(name: str) -> bool:
    pid = _service_pid(name)
    if not pid:
        return False
    log(f"Service {name} is still present with PID {pid}; force-killing stale service process.")
    cp = run(['taskkill', '/F', '/PID', str(pid)], check=False)
    if cp.returncode != 0:
        err = _clean_output((cp.stdout or '') + (cp.stderr or ''))
        log(f"Failed to kill stale service process PID {pid}: {err}")
        return False
    return True


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


def _ensure_agent_log_dir() -> None:
    AGENT_LOG_DIR.mkdir(parents=True, exist_ok=True)


def _is_transient_service_error(text: str) -> bool:
    value = text.lower()
    return (
        "marked for deletion" in value
        or "error 1072" in value
        or "failed 1072" in value
        or "database is locked" in value
    )


def _run_service_command(
    cmd: list[str],
    description: str,
    *,
    attempts: int = 3,
) -> subprocess.CompletedProcess:
    attempts = max(1, attempts)
    for attempt in range(1, attempts + 1):
        cp = run(cmd, check=False)
        if cp.returncode == 0:
            return cp

        stdout = _clean_output(cp.stdout or '')
        stderr = _clean_output(cp.stderr or '')
        combined = "\n".join(part for part in (stdout, stderr) if part)
        if attempt < attempts and _is_transient_service_error(combined):
            log(f"{description} hit a transient service-manager error; retrying ({attempt}/{attempts}).")
            time.sleep(0.75 * attempt)
            continue

        details = [f"{description} failed ({cp.returncode}).", f"Command: {_format_cmd(cmd)}"]
        if stdout:
            details.append(f"Output: {stdout}")
        if stderr:
            details.append(f"Error: {stderr}")
        raise ServiceError("\n".join(details))

    raise ServiceError(f"{description} failed without a result.")


def _get_nssm_parameter(nssm: str, parameter: str) -> list[str] | None:
    cp = run([nssm, 'get', AGENT_SERVICE_NAME, parameter], check=False)
    if cp.returncode != 0:
        output = _clean_output((cp.stdout or '') + (cp.stderr or ''))
        raise ServiceError(
            f"Read service setting {parameter} failed ({cp.returncode})."
            + (f"\n{output}" if output else "")
        )
    value = _clean_output((cp.stdout or '') + (cp.stderr or ''))
    if not value:
        return []
    return [line.strip() for line in value.splitlines() if line.strip()]


def _normalise_parameter_values(parameter: str, values: list[str] | None) -> list[str] | None:
    if values is None:
        return None
    normalised = [str(value).strip() for value in values if str(value).strip()]
    if parameter in {'Application', 'AppDirectory', 'AppStdout', 'AppStderr'}:
        return [os.path.normcase(os.path.normpath(_long_path(value.strip('"')))) for value in normalised]
    if parameter in {'AppEnvironment', 'AppEnvironmentExtra'}:
        return sorted(normalised, key=str.casefold)
    return normalised


def _parameter_matches(parameter: str, actual: list[str] | None, desired: list[str]) -> bool:
    return _normalise_parameter_values(parameter, actual) == _normalise_parameter_values(parameter, desired)


def _write_nssm_parameter(nssm: str, parameter: str, values: list[str] | None, description: str) -> None:
    if values:
        _run_service_command(
            [nssm, 'set', AGENT_SERVICE_NAME, parameter, *values],
            description,
        )
    else:
        _run_service_command(
            [nssm, 'reset', AGENT_SERVICE_NAME, parameter],
            description,
        )


def _apply_nssm_parameter(
    nssm: str,
    parameter: str,
    desired: list[str],
    snapshots: dict[str, list[str] | None],
    changed: list[str],
) -> None:
    current = _get_nssm_parameter(nssm, parameter)
    snapshots[parameter] = current
    if _parameter_matches(parameter, current, desired):
        log(f"Service setting {parameter} is already correct.")
        return

    _write_nssm_parameter(nssm, parameter, desired, f"Set service {parameter}")
    changed.append(parameter)
    verified = _get_nssm_parameter(nssm, parameter)
    if not _parameter_matches(parameter, verified, desired):
        raise ServiceError(
            f"Service setting verification failed for {parameter}. "
            f"Expected {desired!r}, found {verified!r}."
        )


def _rollback_nssm_parameters(
    nssm: str,
    snapshots: dict[str, list[str] | None],
    changed: list[str],
) -> None:
    for parameter in reversed(changed):
        previous = snapshots.get(parameter)
        try:
            _write_nssm_parameter(
                nssm,
                parameter,
                previous,
                f"Restore service {parameter}",
            )
        except Exception as exc:
            log(f"Rollback failed for service setting {parameter}: {exc}")


def _remove_new_service(nssm: str) -> None:
    try:
        run([nssm, 'stop', AGENT_SERVICE_NAME], check=False)
        run([nssm, 'remove', AGENT_SERVICE_NAME, 'confirm'], check=False)
        run(['sc', 'delete', AGENT_SERVICE_NAME], check=False)
        _wait_until_service_removed(AGENT_SERVICE_NAME, 10)
        log("Removed service created by failed configuration attempt.")
    except Exception as exc:
        log(f"Failed to remove service after configuration error: {exc}")


def _require_service_stays_running(stability_seconds: int = 3) -> None:
    deadline = time.time() + max(1, stability_seconds)
    while time.time() < deadline:
        state, _ = _query_service_state_and_pid()
        if state != 'RUNNING':
            raise ServiceError(
                f"Service configuration was applied, but '{AGENT_SERVICE_NAME}' did not remain running "
                f"(state={state}). Check the agent log and service diagnostics."
            )
        time.sleep(0.5)


def _desired_service_settings(env_vars: Dict[str, str]) -> list[tuple[str, list[str]]]:
    env_pairs = sorted(
        (f"{key}={value}" for key, value in env_vars.items() if value),
        key=str.casefold,
    )
    settings: list[tuple[str, list[str]]] = [
        ('Application', [str(AGENT_EXE_PATH)]),
        (
            'Description',
            [f"Beszel agent service managed by {PROJECT_NAME}. Configure and update via {PROJECT_NAME}."],
        ),
        ('AppDirectory', [str(AGENT_DIR)]),
        ('AppStopMethodConsole', ['1500']),
        ('AppStopMethodWindow', ['1500']),
        ('AppStopMethodThreads', ['1500']),
        ('AppStdout', [str(AGENT_LOG_CURRENT_PATH)]),
        ('AppStderr', [str(AGENT_LOG_CURRENT_PATH)]),
        ('AppRotation', ['1']),
        ('AppRotateOnline', ['1']),
        ('AppTimestampLog', ['1']),
        ('AppEnvironment', []),
        ('AppEnvironmentExtra', env_pairs),
        ('Start', ['SERVICE_AUTO_START']),
        ('AppRestartDelay', ['5000']),
    ]
    if AGENT_DISPLAY_NAME != AGENT_SERVICE_NAME:
        settings.insert(0, ('DisplayName', [AGENT_DISPLAY_NAME]))
    return settings


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
    if not _wait_until_service_removed(AGENT_SERVICE_NAME, 10):
        if _kill_service_process_if_present(AGENT_SERVICE_NAME):
            _wait_until_service_removed(AGENT_SERVICE_NAME, 10)
    if _service_exists(AGENT_SERVICE_NAME):
        raise ServiceError(
            f"Service '{AGENT_SERVICE_NAME}' was removed but Windows has not released it yet. "
            "Close the Services window if it is open, wait a few seconds, then apply settings again. "
            "If Windows still reports the service as marked for deletion, restart Windows and apply again."
        )


def create_or_update_service(env_vars: Dict[str, str]) -> None:
    if not AGENT_EXE_PATH.exists():
        raise ServiceError(f'Agent executable not found: {AGENT_EXE_PATH}')
    nssm = _find_nssm()
    service_existed_at_start = _service_exists(AGENT_SERVICE_NAME)
    initial_state = get_service_status() if service_existed_at_start else 'NOT FOUND'
    created_service = False
    snapshots: dict[str, list[str] | None] = {}
    changed: list[str] = []

    if LEGACY_AGENT_SERVICE_NAME != AGENT_SERVICE_NAME and _service_exists(LEGACY_AGENT_SERVICE_NAME):
        run([nssm, 'stop', LEGACY_AGENT_SERVICE_NAME], check=False)
        run([nssm, 'remove', LEGACY_AGENT_SERVICE_NAME, 'confirm'], check=False)
        run(['sc', 'delete', LEGACY_AGENT_SERVICE_NAME], check=False)

    AGENT_DIR.mkdir(parents=True, exist_ok=True)
    if _service_exists(AGENT_SERVICE_NAME):
        _ensure_service_uses_nssm_path(nssm)

    try:
        if not _service_exists(AGENT_SERVICE_NAME):
            _run_service_command(
                [nssm, 'install', AGENT_SERVICE_NAME, str(AGENT_EXE_PATH)],
                "NSSM service install",
            )
            created_service = not service_existed_at_start
            if not _wait_until_service_exists(AGENT_SERVICE_NAME, 5):
                raise ServiceError(f"Service '{AGENT_SERVICE_NAME}' was not visible after NSSM installation.")
        else:
            log(f"Service {AGENT_SERVICE_NAME} already exists; updating configuration.")
        _require_service_exists()
    except Exception:
        if not service_existed_at_start:
            try:
                if created_service or _service_exists(AGENT_SERVICE_NAME):
                    _remove_new_service(nssm)
            except Exception as cleanup_exc:
                log(f"Service cleanup after installation failure could not be completed: {cleanup_exc}")
        raise

    try:
        _ensure_agent_log_dir()
        for parameter, desired in _desired_service_settings(env_vars):
            _apply_nssm_parameter(nssm, parameter, desired, snapshots, changed)

        restart_service(timeout_seconds=15)
        _require_service_stays_running()
    except Exception:
        if created_service:
            _remove_new_service(nssm)
        else:
            _rollback_nssm_parameters(nssm, snapshots, changed)
            try:
                if initial_state == 'RUNNING':
                    restart_service(timeout_seconds=15)
                else:
                    stop_service(timeout_seconds=15)
            except Exception as exc:
                log(f"Restoring service state after rollback failed: {exc}")
        raise


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
