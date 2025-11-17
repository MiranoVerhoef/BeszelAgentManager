from __future__ import annotations
import os
import sys
import shutil
from pathlib import Path

try:
    import ctypes  # type: ignore[attr-defined]
except Exception:
    ctypes = None  # type: ignore

from .constants import PROJECT_NAME, PROGRAM_FILES, DATA_DIR, LOCK_PATH
from .util import log, run
from . import shortcut as shortcut_mod
from .defender import ensure_defender_exclusion_for_manager


def _is_windows() -> bool:
    return os.name == "nt"


def _is_admin() -> bool:
    if not _is_windows() or ctypes is None:
        return False
    try:
        return ctypes.windll.shell32.IsUserAnAdmin() != 0  # type: ignore[attr-defined]
    except Exception:
        return False


def _run_as_admin(exe: str, args: list[str]) -> None:
    if not _is_windows() or ctypes is None:
        return
    params = " ".join(f'"{a}"' for a in args)
    ctypes.windll.shell32.ShellExecuteW(  # type: ignore[attr-defined]
        None,
        "runas",
        exe,
        params,
        None,
        1,
    )


def _message_box(text: str, title: str, flags: int) -> int:
    if not _is_windows() or ctypes is None:
        return 0
    return ctypes.windll.user32.MessageBoxW(  # type: ignore[attr-defined]
        None,
        text,
        title,
        flags,
    )


def _ensure_single_instance() -> None:
    """Prevent multiple manager instances; optionally kill the old one.

    Uses a simple lock file under DATA_DIR containing the previous PID.
    If another PID is found, prompts the user to terminate it.
    """
    if not _is_windows():
        return

    DATA_DIR.mkdir(parents=True, exist_ok=True)
    pid = os.getpid()

    if LOCK_PATH.exists():
        try:
            old_text = LOCK_PATH.read_text(encoding="utf-8").strip()
            old_pid = int(old_text) if old_text else None
        except Exception:
            old_pid = None

        if old_pid and old_pid != pid:
            msg = (
                f"Another instance of {PROJECT_NAME} may already be running (PID {old_pid}).\n\n"
                "Do you want to terminate that instance and continue?"
            )
            # MB_YESNO | MB_ICONWARNING
            res = _message_box(msg, PROJECT_NAME, 0x00000004 | 0x00000030)
            if res == 6:  # IDYES
                try:
                    # Try via taskkill to be robust on Windows
                    run(["taskkill", "/PID", str(old_pid), "/F"], check=False)
                    log(f"Requested termination of old instance PID {old_pid}")
                except Exception as exc:
                    log(f"Failed to terminate old instance PID {old_pid}: {exc}")
            else:
                # User chose not to run a second instance
                sys.exit(0)

    try:
        LOCK_PATH.write_text(str(pid), encoding="utf-8")
    except Exception as exc:
        log(f"Failed to write lock file {LOCK_PATH}: {exc}")


def ensure_elevated_and_location() -> None:
    """Ensure we are elevated, optionally relocate to Program Files, set shortcuts & lock.

    - If not running as admin, re-launch as admin and exit current process.
    - If running as a frozen EXE, offer to move to C:\Program Files\BeszelAgentManager
      and re-launch from there.
    - Always ensure a Start Menu shortcut and request a Defender exclusion for the
      canonical install directory.
    - Enforce a single-instance lock for the manager.
    """
    if not _is_windows():
        return

    if not _is_admin():
        exe = sys.executable
        args = sys.argv
        _run_as_admin(exe, args)
        sys.exit(0)

    # Only do relocation logic for frozen executables
    if getattr(sys, "frozen", False):
        exe_path = Path(sys.executable).resolve()
        target_dir = Path(PROGRAM_FILES) / PROJECT_NAME
        target_dir.mkdir(parents=True, exist_ok=True)
        target_exe = (target_dir / exe_path.name).resolve()

        if exe_path != target_exe:
            msg = (
                f"BeszelAgentManager is recommended to run from:\n\n"
                f"{target_exe}\n\n"
                f"Move the current executable there and run from that location?"
            )
            # MB_YESNO | MB_ICONQUESTION
            res = _message_box(msg, PROJECT_NAME, 0x00000004 | 0x00000020)
            if res == 6:  # IDYES
                try:
                    shutil.copy2(str(exe_path), str(target_exe))
                    log(f"Copied executable to {target_exe}")
                except Exception as exc:
                    _message_box(
                        f"Failed to copy to {target_exe}:\n{exc}",
                        PROJECT_NAME,
                        0x00000010,  # MB_ICONERROR
                    )
                else:
                    _run_as_admin(str(target_exe), sys.argv[1:])
                sys.exit(0)
            # If user chose No, continue running from current location

    # Common setup for the elevated, active instance
    shortcut_mod.ensure_start_menu_shortcut()
    ensure_defender_exclusion_for_manager()
    _ensure_single_instance()
