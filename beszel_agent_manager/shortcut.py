from __future__ import annotations
import os
import sys
from pathlib import Path
from .util import log, run
from .constants import PROJECT_NAME


def _start_menu_programs_dir() -> Path:
    program_data = os.environ.get("ProgramData", r"C:\ProgramData")
    return Path(program_data) / "Microsoft" / "Windows" / "Start Menu" / "Programs"


def get_shortcut_path() -> Path:
    return _start_menu_programs_dir() / f"{PROJECT_NAME}.lnk"


def ensure_start_menu_shortcut() -> None:
    if os.name != "nt":
        return
    shortcut = get_shortcut_path()
    if shortcut.exists():
        return
    create_start_menu_shortcut()


def create_start_menu_shortcut() -> None:
    if os.name != "nt":
        return

    target = Path(sys.executable).resolve()
    shortcut = get_shortcut_path()
    shortcut.parent.mkdir(parents=True, exist_ok=True)

    target_str = str(target).replace("'", "''")
    shortcut_str = str(shortcut).replace("'", "''")
    working_dir_str = str(target.parent).replace("'", "''")

    ps = (
        "$WScriptShell = New-Object -ComObject WScript.Shell; "
        f"$shortcut = $WScriptShell.CreateShortcut('{shortcut_str}'); "
        f"$shortcut.TargetPath = '{target_str}'; "
        f"$shortcut.WorkingDirectory = '{working_dir_str}'; "
        "$shortcut.WindowStyle = 1; "
        f"$shortcut.IconLocation = '{target_str},0'; "
        "$shortcut.Save();"
    )

    try:
        run([
            "powershell",
            "-NoLogo",
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-Command", ps,
        ], check=False)
        log(f"Created Start Menu shortcut at {shortcut}")
    except Exception as exc:
        log(f"Failed to create Start Menu shortcut: {exc}")


def remove_start_menu_shortcut() -> None:
    if os.name != "nt":
        return
    shortcut = get_shortcut_path()
    try:
        if shortcut.exists():
            shortcut.unlink()
            log(f"Removed Start Menu shortcut {shortcut}")
    except Exception as exc:
        log(f"Failed to remove Start Menu shortcut: {exc}")
