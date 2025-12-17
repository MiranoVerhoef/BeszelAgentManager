from __future__ import annotations

import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Optional

from .constants import PROJECT_NAME, PROGRAM_FILES, DATA_DIR, LOCK_PATH
from .util import log, ensure_data_dir

# Flag file so we only run ACL changes once
ACL_DONE_FLAG = DATA_DIR / "acl_done.flag"


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def is_admin() -> bool:
    """
    Return True if the current process has admin rights (on Windows).
    On non-Windows platforms this always returns True.
    """
    if os.name != "nt":
        return True

    try:
        import ctypes  # type: ignore[attr-defined]

        return bool(ctypes.windll.shell32.IsUserAnAdmin())  # type: ignore[attr-defined]
    except Exception as exc:
        log(f"is_admin check failed: {exc}")
        return False


def _get_current_exe() -> Optional[Path]:
    """
    Return the current executable path if running under PyInstaller,
    otherwise None (when running from source).
    """
    if not getattr(sys, "frozen", False):
        return None
    return Path(sys.executable).resolve()


def _run_hidden(cmd, check: bool = False, capture_output: bool = False):
    """
    Run a console command in a *hidden* window on Windows.
    On non-Windows it just runs normally.
    """
    kwargs = {
        "shell": False,
        "stdout": subprocess.DEVNULL,
        "stderr": subprocess.DEVNULL,
    }

    if capture_output:
        kwargs["stdout"] = subprocess.PIPE
        kwargs["stderr"] = subprocess.PIPE
        kwargs["text"] = True

    if os.name == "nt":
        # Hide the console window
        kwargs["creationflags"] = getattr(subprocess, "CREATE_NO_WINDOW", 0)

    try:
        return subprocess.run(cmd, check=check, **kwargs)
    except Exception as exc:
        log(f"Error running command {cmd}: {exc}")
        return None


# ---------------------------------------------------------------------------
# Program Files self-install / ACL
# ---------------------------------------------------------------------------

def _adjust_acl(path: Path) -> None:
    """
    Grant 'Users' Modify rights on the given directory (and children) so the
    non-admin GUI can read/write in Program Files / ProgramData later.
    Only does anything on Windows, and runs icacls in a hidden console.
    """
    if os.name != "nt":
        return

    try:
        path.mkdir(parents=True, exist_ok=True)
    except Exception as exc:
        log(f"Failed to create directory {path} before ACL adjustment: {exc}")
        return

    cmd = [
        "icacls",
        str(path),
        "/grant",
        "Users:(OI)(CI)M",
        "/T",
        "/C",
    ]
    proc = _run_hidden(cmd, check=True)
    if proc is not None and proc.returncode == 0:
        log(f"Adjusted ACL on {path} (Users: Modify).")
    else:
        log(f"Failed to adjust ACL on {path}.")


def _copy_to_program_files(exe_path: Path) -> Path:
    """
    Copy the current executable into
    C:\\Program Files\\<PROJECT_NAME>\\<PROJECT_NAME>.exe
    and return the new path.
    """
    target_dir = Path(PROGRAM_FILES) / PROJECT_NAME
    target_dir.mkdir(parents=True, exist_ok=True)

    target_exe = target_dir / f"{PROJECT_NAME}.exe"
    if exe_path.resolve() != target_exe.resolve():
        shutil.copy2(exe_path, target_exe)
        log(f"Copied manager executable to {target_exe}")
    else:
        log("Executable is already in Program Files; skipping copy.")

    return target_exe


def _schedule_delete_file(path: Path) -> None:
    """Best-effort delete of a file after this process exits.

    We can't truly "move" a running executable. Instead we copy it to Program Files,
    then schedule deletion of the original file once the process exits.
    """
    if os.name != "nt":
        return
    try:
        # Fire-and-forget. The elevated bootstrap process must exit before the file can be deleted.
        # Also retries a few times and attempts to remove the parent folder if it becomes empty.
        cmd_str = (
            f'cmd.exe /C "(timeout /T 3 /NOBREAK >NUL) & '
            f'(for /L %i in (1,1,30) do (del /F /Q "{path}" >NUL 2>&1 && goto :done) & '
            f'timeout /T 1 /NOBREAK >NUL) & '
            f':done & (rmdir "{path.parent}" 2>NUL)"'
        )
        creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        subprocess.Popen(cmd_str, shell=True, creationflags=creationflags)
        log(f"Scheduled deletion of original executable: {path}")
    except Exception as exc:
        log(f"Failed to schedule deletion of original executable {path}: {exc}")


def _run_elevated_again(exe_path: Path) -> None:
    """
    Relaunch this executable as administrator (UAC prompt).
    """
    if os.name != "nt":
        return

    try:
        import ctypes  # type: ignore[attr-defined]

        rc = ctypes.windll.shell32.ShellExecuteW(  # type: ignore[attr-defined]
            None,
            "runas",
            str(exe_path),
            None,
            None,
            1,
        )
        if rc <= 32:
            log(f"ShellExecuteW(runas) failed with code {rc}")
        else:
            log("Successfully requested elevation via ShellExecuteW(runas).")
    except Exception as exc:
        log(f"Failed to relaunch as admin via ShellExecuteW: {exc}")


def _ask_move_and_elevate(exe_path: Path) -> None:
    """
    Show a small Windows message box asking if we may move to Program Files and
    run elevated once. If user clicks Yes, we relaunch as admin and exit.
    """
    if os.name != "nt":
        return

    try:
        import ctypes  # type: ignore[attr-defined]

        text = (
            f"{PROJECT_NAME} needs to move itself to:\n"
            f"  {PROGRAM_FILES}\\{PROJECT_NAME}\n\n"
            "and run once with administrator rights to set permissions.\n\n"
            "Click Yes to continue, or No to exit."
        )

        MB_ICONINFORMATION = 0x40
        MB_YESNO = 0x04
        IDYES = 6

        res = ctypes.windll.user32.MessageBoxW(  # type: ignore[attr-defined]
            0,
            text,
            PROJECT_NAME,
            MB_ICONINFORMATION | MB_YESNO,
        )
        if res == IDYES:
            _run_elevated_again(exe_path)
        else:
            log("User declined elevation / move to Program Files.")
    except Exception as exc:
        log(f"Failed to show elevation/move message box: {exc}")

    # Always exit current non-elevated instance after asking.
    sys.exit(0)


def ensure_elevated_and_location() -> None:
    """
    Windows-only bootstrap that ensures:
      - The manager EXE lives under Program Files\\<PROJECT_NAME>
      - One elevated run has set ACLs on Program Files and ProgramData
      - ProgramData directory exists

    On non-Windows or when running from source (python main.py), this is a no-op
    except for making sure the data directory exists.
    """
    # Always ensure data dir exists
    ensure_data_dir()

    if os.name != "nt":
        return

    exe_path = _get_current_exe()
    if exe_path is None:
        # Running from source
        return

    target_dir = Path(PROGRAM_FILES) / PROJECT_NAME
    target_exe = target_dir / f"{PROJECT_NAME}.exe"

    # If we're not in Program Files at all, we must first ask to elevate.
    if exe_path.resolve() != target_exe.resolve():
        if not is_admin():
            _ask_move_and_elevate(exe_path)
            return  # _ask_move_and_elevate exits

        # Elevated and not yet in Program Files -> perform the move
        new_exe = _copy_to_program_files(exe_path)

        if not ACL_DONE_FLAG.exists():
            _adjust_acl(target_dir)
            _adjust_acl(DATA_DIR.parent)
            _adjust_acl(DATA_DIR)
            try:
                ACL_DONE_FLAG.parent.mkdir(parents=True, exist_ok=True)
                ACL_DONE_FLAG.write_text("ok", encoding="utf-8")
            except Exception as exc:
                log(f"Failed to write ACL flag: {exc}")

        # Start non-elevated Program Files instance and exit.
        try:
            subprocess.Popen([str(new_exe)], shell=False)
            log(f"Started Program Files instance {new_exe} and exiting elevated instance.")
            # Delete the original executable (best-effort) so behavior is closer to a true move.
            _schedule_delete_file(exe_path)
        except Exception as exc:
            log(f"Failed to start Program Files instance: {exc}")
        sys.exit(0)

    # We *are* already in Program Files.
    # If we are elevated, take the chance to adjust ACLs (once).
    if is_admin() and not ACL_DONE_FLAG.exists():
        _adjust_acl(target_dir)
        _adjust_acl(DATA_DIR.parent)
        _adjust_acl(DATA_DIR)
        try:
            ACL_DONE_FLAG.parent.mkdir(parents=True, exist_ok=True)
            ACL_DONE_FLAG.write_text("ok", encoding="utf-8")
        except Exception as exc:
            log(f"Failed to write ACL flag: {exc}")


# ---------------------------------------------------------------------------
# Single-instance enforcement
# ---------------------------------------------------------------------------

def _pid_alive(pid: int) -> bool:
    if pid <= 0:
        return False

    if os.name == "nt":
        try:
            proc = _run_hidden(
                ["tasklist", "/FI", f"PID eq {pid}"],
                check=False,
                capture_output=True,
            )
            if not proc:
                return False
            text = proc.stdout or ""
            return str(pid) in text
        except Exception as exc:
            log(f"Failed to check PID {pid} via tasklist: {exc}")
            return False
    else:
        try:
            # On POSIX, sending signal 0 just checks for existence.
            os.kill(pid, 0)
            return True
        except OSError:
            return False


def _ask_kill_existing(pid: int) -> bool:
    """
    Ask the user whether to kill the existing instance with the given PID.

    Returns True if we should attempt to kill it, False if we should exit.
    """
    if os.name != "nt":
        # On non-Windows, just kill without asking.
        return True

    try:
        import ctypes  # type: ignore[attr-defined]

        text = (
            f"Another instance of {PROJECT_NAME} is already running (PID {pid}).\n\n"
            "Do you want to close the existing instance and start a new one?"
        )

        MB_ICONQUESTION = 0x20
        MB_YESNO = 0x04
        IDYES = 6

        res = ctypes.windll.user32.MessageBoxW(  # type: ignore[attr-defined]
            0,
            text,
            PROJECT_NAME,
            MB_ICONQUESTION | MB_YESNO,
        )
        return res == IDYES
    except Exception as exc:
        log(f"Failed to show single-instance message box: {exc}")
        return False


def ensure_single_instance() -> None:
    """
    Enforce single-instance behavior using a PID lock file at LOCK_PATH.

    If a live PID is found in the lock file, the user is asked whether to kill
    that instance. If they decline, this process exits. Stale locks are
    replaced silently.
    """
    try:
        LOCK_PATH.parent.mkdir(parents=True, exist_ok=True)
    except Exception as exc:
        log(f"Failed to create lock directory {LOCK_PATH.parent}: {exc}")

    existing_pid: Optional[int] = None

    if LOCK_PATH.exists():
        try:
            content = LOCK_PATH.read_text(encoding="utf-8").strip()
            if content:
                existing_pid = int(content)
        except Exception as exc:
            log(f"Failed to read existing lock file {LOCK_PATH}: {exc}")

    if existing_pid is not None and _pid_alive(existing_pid):
        # Another instance is alive
        if _ask_kill_existing(existing_pid):
            try:
                if os.name == "nt":
                    _run_hidden(["taskkill", "/PID", str(existing_pid), "/F"])
                else:
                    os.kill(existing_pid, 9)
                log(f"Killed existing {PROJECT_NAME} instance PID {existing_pid}.")
            except Exception as exc:
                log(f"Failed to kill existing PID {existing_pid}: {exc}")
        else:
            log(f"Existing instance PID {existing_pid} kept; exiting new instance.")
            sys.exit(0)
    elif existing_pid is not None:
        # Lock existed but process is gone – stale lock
        log(f"Stale instance lock detected for PID {existing_pid}, replacing with {os.getpid()}.")

    # Write our own PID
    try:
        LOCK_PATH.write_text(str(os.getpid()), encoding="utf-8")
        log(f"Instance lock set to PID {os.getpid()}")
    except Exception as exc:
        log(f"Failed to write instance lock file {LOCK_PATH}: {exc}")
