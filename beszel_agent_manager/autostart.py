from __future__ import annotations
import os
import sys
from pathlib import Path

from .constants import PROJECT_NAME, PROGRAM_FILES

if os.name == "nt":
    import winreg  # type: ignore


def _get_exe_path() -> Path:
    if getattr(sys, "frozen", False):
        exe = Path(sys.executable).resolve()
    else:
        exe = Path(sys.argv[0]).resolve()

    pf_exe = Path(PROGRAM_FILES) / PROJECT_NAME / exe.name
    if pf_exe.exists():
        return pf_exe
    return exe


def is_autostart_enabled() -> bool:
    if os.name != "nt":
        return False
    try:
        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            r"Software\Microsoft\Windows\CurrentVersion\Run",
        ) as key:
            value, _ = winreg.QueryValueEx(key, PROJECT_NAME)
            return bool(value)
    except FileNotFoundError:
        return False
    except OSError:
        return False


def set_autostart(enabled: bool, start_hidden: bool) -> None:
    """Create or remove the Run entry.

    When start_hidden is True, append --hidden so the GUI starts to tray only.
    """
    if os.name != "nt":
        return

    exe_path = _get_exe_path()
    cmd = f'"{exe_path}"'
    if start_hidden:
        cmd += " --hidden"

    run_key = r"Software\Microsoft\Windows\CurrentVersion\Run"

    try:
        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            run_key,
            0,
            winreg.KEY_SET_VALUE,
        ) as key:
            if enabled:
                winreg.SetValueEx(key, PROJECT_NAME, 0, winreg.REG_SZ, cmd)
            else:
                try:
                    winreg.DeleteValue(key, PROJECT_NAME)
                except FileNotFoundError:
                    pass
    except FileNotFoundError:
        if enabled:
            try:
                with winreg.CreateKey(
                    winreg.HKEY_CURRENT_USER,
                    run_key,
                ) as key:
                    winreg.SetValueEx(key, PROJECT_NAME, 0, winreg.REG_SZ, cmd)
            except OSError:
                pass
    except OSError:
        # Non-fatal; just don't crash if registry access fails.
        pass
