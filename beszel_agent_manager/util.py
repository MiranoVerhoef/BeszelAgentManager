from __future__ import annotations
import datetime
import subprocess
import os
from .constants import DATA_DIR, LOG_PATH

DEBUG_LOGGING = False

if os.name == "nt":
    CREATE_NO_WINDOW = subprocess.CREATE_NO_WINDOW
else:
    CREATE_NO_WINDOW = 0


def ensure_data_dir() -> None:
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    LOG_PATH.touch(exist_ok=True)


def log(message: str) -> None:
    ensure_data_dir()
    ts = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    line = f"[{ts}] {message}"
    try:
        with LOG_PATH.open("a", encoding="utf-8") as f:
            f.write(line + "\n")
    except OSError:
        print(line)


def set_debug_logging(enabled: bool) -> None:
    global DEBUG_LOGGING
    DEBUG_LOGGING = bool(enabled)
    log(f"Debug logging set to {DEBUG_LOGGING}")


def run(cmd, check: bool = True) -> subprocess.CompletedProcess:
    """Wrapper around subprocess.run used everywhere (sc, netsh, nssm, PowerShell)."""
    line = " ".join(str(c) for c in cmd)
    kwargs = {
        "capture_output": True,
        "text": True,
        "shell": False,
    }
    if CREATE_NO_WINDOW:
        kwargs["creationflags"] = CREATE_NO_WINDOW

    if DEBUG_LOGGING:
        log(f"Running: {line}")
        cp = subprocess.run(cmd, **kwargs)
        if cp.stdout:
            log("stdout: " + cp.stdout.strip())
        if cp.stderr:
            log("stderr: " + cp.stderr.strip())
    else:
        cp = subprocess.run(cmd, **kwargs)

    if check and cp.returncode != 0:
        raise RuntimeError(
            f"Command failed ({cp.returncode}): {line}\n"
            f"stdout: {cp.stdout}\nstderr: {cp.stderr}"
        )
    return cp
