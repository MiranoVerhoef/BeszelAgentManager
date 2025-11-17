from __future__ import annotations
from pathlib import Path
import sys

try:
    import winreg  # type: ignore[attr-defined]
except ImportError:
    winreg = None  # type: ignore

from .constants import PROJECT_NAME, DATA_DIR
from .util import log

RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
VALUE_NAME = PROJECT_NAME


def _get_executable_path() -> Path:
    if getattr(sys, "frozen", False):
        return Path(sys.executable)
    return Path(sys.argv[0]).resolve()


def is_autostart_enabled() -> bool:
    if winreg is None:
        return False
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_READ) as key:
            value, _ = winreg.QueryValueEx(key, VALUE_NAME)
            return bool(value)
    except OSError:
        return False


def set_autostart(enabled: bool) -> None:
    if winreg is None:
        log("winreg not available, cannot configure autostart.")
        return
    exe_path = _get_executable_path()
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as key:
            if enabled:
                winreg.SetValueEx(key, VALUE_NAME, 0, winreg.REG_SZ, str(exe_path))
                log(f"Autostart enabled for {exe_path}")
            else:
                try:
                    winreg.DeleteValue(key, VALUE_NAME)
                    log("Autostart disabled.")
                except FileNotFoundError:
                    pass
    except FileNotFoundError:
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
            if enabled:
                winreg.SetValueEx(key, VALUE_NAME, 0, winreg.REG_SZ, str(exe_path))
