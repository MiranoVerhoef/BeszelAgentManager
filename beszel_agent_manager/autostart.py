from __future__ import annotations

import os
import sys
from pathlib import Path

from .constants import PROJECT_NAME
from .util import log

if os.name == "nt":
    import winreg  # type: ignore[import]


RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
VALUE_NAME = PROJECT_NAME


def _get_exe_path() -> Path:
    """
    Return the path to the currently running executable/script.
    Works both for PyInstaller bundles and normal python.
    """
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve()
    return Path(sys.argv[0]).resolve()


def _open_run_key_writable():
    """
    Open (or create) HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run
    with write access.
    """
    try:
        return winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            RUN_KEY,
            0,
            winreg.KEY_ALL_ACCESS,
        )
    except FileNotFoundError:
        return winreg.CreateKey(winreg.HKEY_CURRENT_USER, RUN_KEY)


def get_autostart_state() -> tuple[bool, bool]:
    """
    Returns (enabled, start_hidden) for the BeszelAgentManager autostart entry.

    enabled      -> whether a Run entry exists for this app
    start_hidden -> True if the stored command line contains '--hidden'
    """
    if os.name != "nt":
        return False, False

    try:
        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            RUN_KEY,
            0,
            winreg.KEY_READ,
        ) as key:
            val, _ = winreg.QueryValueEx(key, VALUE_NAME)
    except FileNotFoundError:
        return False, False
    except OSError as exc:
        log(f"get_autostart_state: failed to read Run key: {exc}")
        return False, False

    val_str = str(val or "")
    start_hidden = "--hidden" in val_str
    return True, start_hidden


def is_autostart_enabled() -> bool:
    """
    Backwards-compatible helper: just returns whether a Run entry exists.
    """
    enabled, _ = get_autostart_state()
    return enabled


def set_autostart(enabled: bool, start_hidden: bool) -> None:
    """
    Create/update/remove the autostart (Run) entry.

    If start_hidden is True, ' --hidden' is appended to the command line so
    the manager knows that *this particular run* should start tray-only.
    """
    if os.name != "nt":
        return

    key = _open_run_key_writable()
    try:
        if enabled:
            exe_path = _get_exe_path()
            cmd = f'"{exe_path}"'
            if start_hidden:
                cmd += " --hidden"

            winreg.SetValueEx(key, VALUE_NAME, 0, winreg.REG_SZ, cmd)
            log(f"Autostart enabled: {VALUE_NAME} = {cmd}")
        else:
            try:
                winreg.DeleteValue(key, VALUE_NAME)
                log("Autostart disabled (Run key value removed).")
            except FileNotFoundError:
                log("Autostart disable requested but Run key value did not exist.")
    finally:
        winreg.CloseKey(key)
