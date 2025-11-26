from __future__ import annotations
import os
import sys
import shutil
import subprocess
from pathlib import Path

try:
    import ctypes  # type: ignore[attr-defined]
except Exception:
    ctypes = None  # type: ignore

from .constants import PROJECT_NAME, PROGRAM_FILES, DATA_DIR, LOCK_PATH
from .util import log, run
from . import shortcut as shortcut_mod


def _is_windows() -> bool:
    return os.name == "nt"


def _is_admin() -> bool:
    if not _is_windows() or ctypes is None:
        return False
    try:
        return ctypes.windll.shell32.IsUserAnAdmin() != 0  # type: ignore[attr-defined]
    except Exception:
        return False


def is_admin() -> bool:
    """Public helper so the GUI can check if the process is elevated."""
    return _is_admin()


def _message_box(text: str, title: str, flags: int) -> int:
    if not _is_windows() or ctypes is None:
        return 0
    return ctypes.windll.user32.MessageBoxW(  # type: ignore[attr-defined]
        None,
        text,
        title,
        flags,
    )


def _run_as_admin(exe_path: Path) -> None:
    """
    Relaunch the current executable with elevation (UAC).
    Used only for the very first install into Program Files.
    """
    if not _is_windows() or ctypes is None:
        return
    try:
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
    except Exception as exc:
        log(f"Failed to relaunch as admin: {exc}")


def _is_pid_running(pid: int) -> bool:
    """
    Check if a PID is currently running on Windows using the native API.

    This avoids parsing localized tasklist output and fixes the
    "another instance" popup after reboot.
    """
    if not _is_windows() or ctypes is None:
        return False

    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    SYNCHRONIZE = 0x00100000
    access = PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE

    try:
        kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
        handle = kernel32.OpenProcess(access, False, pid)
        if not handle:
            return False

        try:
            exit_code = ctypes.c_ulong()
            if not kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code)):
                # If we cannot get the exit code, be conservative and assume running.
                return True
            STILL_ACTIVE = 259
            return exit_code.value == STILL_ACTIVE
        finally:
            kernel32.CloseHandle(handle)
    except Exception as exc:
        log(f"Failed to check if PID {pid} is running: {exc}")
        # If we really can't tell, treat as running to avoid accidental duplicate managers.
        return True


def _ensure_single_instance() -> None:
    """Prevent multiple manager instances; optionally kill the old one.

    Uses a simple lock file under DATA_DIR containing the previous PID.
    If another PID is found and is still running, prompts the user to terminate it.
    If the PID is stale (no such process), silently take over the lock.
    """
    if not _is_windows():
        return

    DATA_DIR.mkdir(parents=True, exist_ok=True)
    pid = os.getpid()
    stale = False

    if LOCK_PATH.exists():
        try:
            old_text = LOCK_PATH.read_text(encoding="utf-8").strip()
            old_pid = int(old_text) if old_text else None
        except Exception:
            old_pid = None

        if old_pid and old_pid != pid:
            if not _is_pid_running(old_pid):
                # Stale lock from previous session / reboot: just take over.
                log(f"Stale instance lock detected for PID {old_pid}, replacing with {pid}.")
                stale = True
            else:
                msg = (
                    f"Another instance of {PROJECT_NAME} may already be running (PID {old_pid}).\n\n"
                    "Do you want to terminate that instance and continue?"
                )
                # MB_YESNO | MB_ICONWARNING
                res = _message_box(msg, PROJECT_NAME, 0x00000004 | 0x00000030)
                if res == 6:  # IDYES
                    try:
                        run(["taskkill", "/PID", str(old_pid), "/F"], check=False)
                        log(f"Requested termination of old instance PID {old_pid}")
                    except Exception as exc:
                        log(f"Failed to terminate old instance PID {old_pid}: {exc}")
                else:
                    sys.exit(0)

    try:
        LOCK_PATH.write_text(str(pid), encoding="utf-8")
        if not stale:
            log(f"Instance lock set to PID {pid}")
    except Exception as exc:
        log(f"Failed to write lock file {LOCK_PATH}: {exc}")


def _grant_users_modify(path: Path, label: str) -> None:
    """
    Best-effort: grant Users modify permission on the given folder (and children).
    This allows the non-admin user to write logs / config without UAC prompts.
    """
    try:
        path.mkdir(parents=True, exist_ok=True)
    except Exception as exc:
        log(f"Failed to create {label} directory {path}: {exc}")
        return

    try:
        run(
            [
                "icacls",
                str(path),
                "/grant",
                "Users:(OI)(CI)M",
                "/T",
                "/C",
            ],
            check=False,
        )
        log(f"Adjusted ACL on {label} {path} (Users: Modify).")
    except Exception as exc:
        log(f"Failed to set ACL on {label} {path}: {exc}")


def ensure_elevated_and_location() -> None:
    """Startup hook.

    Behaviour:
    - On first run when the manager is NOT in C:\\Program Files\\BeszelAgentManager,
      relaunch once with UAC so it can copy itself there and fix permissions.
    - In that elevated run, copy to Program Files, grant Users modify on both
      Program Files\\BeszelAgentManager and ProgramData\\BeszelAgentManager, then
      start the Program Files copy and exit.
    - Once running from that Program Files location, DO NOT auto-elevate anymore,
      so there is no UAC popup on normal startup / autostart.
    - Always ensure a Start Menu shortcut and single-instance lock.
    """
    if not _is_windows():
        return

    if getattr(sys, "frozen", False):
        exe_path = Path(sys.executable).resolve()
    else:
        exe_path = Path(sys.argv[0]).resolve()

    target_dir = Path(PROGRAM_FILES) / PROJECT_NAME
    target_exe = target_dir / exe_path.name

    # First-install move into Program Files
    if getattr(sys, "frozen", False) and exe_path != target_exe:
        if not _is_admin():
            msg = (
                f"{PROJECT_NAME} can install itself to:\n\n"
                f"{target_exe}\n\n"
                "This requires administrator permissions once. After that, it will run "
                "without UAC prompts.\n\n"
                "Do you want to continue?"
            )
            res = _message_box(msg, PROJECT_NAME, 0x00000004 | 0x00000020)  # YESNO | QUESTION
            if res == 6:  # IDYES
                _run_as_admin(exe_path)
            else:
                log("User declined elevation for initial install.")
            sys.exit(0)
        else:
            try:
                target_dir.mkdir(parents=True, exist_ok=True)
                shutil.copy2(str(exe_path), str(target_exe))
                log(f"Copied manager executable to {target_exe}")
            except Exception as exc:
                _message_box(
                    f"Failed to copy to {target_exe}:\n{exc}",
                    PROJECT_NAME,
                    0x00000010,  # MB_ICONERROR
                )
            else:
                _grant_users_modify(target_dir, "Program Files folder")
                _grant_users_modify(DATA_DIR, "ProgramData folder")

                try:
                    creationflags = 0
                    if hasattr(subprocess, "CREATE_NO_WINDOW"):
                        creationflags = subprocess.CREATE_NO_WINDOW
                    subprocess.Popen([str(target_exe)], creationflags=creationflags)
                    log(
                        f"Started Program Files instance {target_exe} and exiting elevated instance."
                    )
                except Exception as exc:
                    log(f"Failed to start Program Files instance {target_exe}: {exc}")
                finally:
                    sys.exit(0)

    # Normal runs from Program Files or elsewhere
    shortcut_mod.ensure_start_menu_shortcut()
    _ensure_single_instance()
