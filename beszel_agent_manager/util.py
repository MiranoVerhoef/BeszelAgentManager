from __future__ import annotations
import datetime
import subprocess
import os
from .constants import DATA_DIR, LOG_PATH, RUNTIME_TMP_DIR
from .manager_logs import rotate_if_needed

DEBUG_LOGGING = False

if os.name == "nt":
    CREATE_NO_WINDOW = subprocess.CREATE_NO_WINDOW
else:
    CREATE_NO_WINDOW = 0


def ensure_data_dir() -> None:
    """
    Ensure the data directory and log file exist.

    Any PermissionError is treated as non-fatal: logging will just fall back
    to printing to stdout instead of crashing the whole app.
    """
    try:
        DATA_DIR.mkdir(parents=True, exist_ok=True)
    except PermissionError:
        # Can't create data dir; nothing we can do, but don't crash.
        return
    except OSError:
        # Some other filesystem error; also non-fatal.
        return

    # Best-effort: prepare a stable PyInstaller onefile extraction directory.
    # This helps on machines where AV/Defender interferes with the default user-temp
    # _MEI... folder extraction.
    try:
        ensure_runtime_tmp_dir()
    except Exception:
        pass

    try:
        LOG_PATH.touch(exist_ok=True)
    except PermissionError:
        # No permission to create/touch the log file; silently ignore.
        pass
    except OSError:
        pass

def ensure_runtime_tmp_dir() -> None:
    """Best-effort create and ACL-fix the PyInstaller onefile runtime tmp dir.

    If you build the manager with:
      --runtime-tmpdir "C:\\ProgramData\\BeszelAgentManager\\tmp"

    then the PyInstaller bootloader will extract into this folder.
    Some systems run scheduled tasks / GUI actions under different users; granting
    BUILTIN\Users modify rights makes extraction more reliable.
    """
    try:
        RUNTIME_TMP_DIR.mkdir(parents=True, exist_ok=True)
    except Exception:
        return

    if os.name != "nt":
        return

    # Grant BUILTIN\Users modify rights using the SID to avoid localization issues.
    # SID S-1-5-32-545 = Builtin Users.
    try:
        subprocess.run(
            [
                "icacls",
                str(RUNTIME_TMP_DIR),
                "/grant",
                "*S-1-5-32-545:(OI)(CI)M",
                "/T",
                "/C",
            ],
            capture_output=True,
            text=True,
            creationflags=CREATE_NO_WINDOW if CREATE_NO_WINDOW else 0,
        )
    except Exception:
        return


def log(message: str) -> None:
    ensure_data_dir()
    # Automatic daily rotation of manager.log
    try:
        rotate_if_needed(force=False)
    except Exception:
        pass
    ts = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    line = f"[{ts}] {message}"
    try:
        with LOG_PATH.open("a", encoding="utf-8") as f:
            f.write(line + "\n")
    except OSError:
        # Fall back to stdout if we can't write the log file.
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


def try_write_event_log(message: str, *, event_id: int = 2600, level: str = "INFORMATION") -> None:
    """Best-effort write to Windows Application event log.

    Uses `eventcreate.exe` because it works without pre-registering an event source.
    This is best-effort and never raises.
    """
    if os.name != "nt":
        return
    try:
        lvl = (level or "INFORMATION").upper()
        if lvl not in ("INFORMATION", "WARNING", "ERROR"):
            lvl = "INFORMATION"
        # eventcreate requires an integer 1..1000 for /ID
        eid = int(event_id)
        if eid < 1:
            eid = 1
        if eid > 1000:
            eid = 1000
        subprocess.run(
            [
                "eventcreate",
                "/L",
                "APPLICATION",
                "/T",
                lvl,
                "/ID",
                str(eid),
                "/SO",
                "BeszelAgentManager",
                "/D",
                str(message),
            ],
            capture_output=True,
            text=True,
            creationflags=CREATE_NO_WINDOW if CREATE_NO_WINDOW else 0,
        )
    except Exception:
        return
