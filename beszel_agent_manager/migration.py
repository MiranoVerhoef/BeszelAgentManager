from __future__ import annotations

import os
import re
import subprocess
import sys
from pathlib import Path

from .constants import DATA_DIR, MANAGER_EXE_PATH, MANAGER_INSTALL_DIR, PROJECT_NAME
from .shortcut import create_start_menu_shortcut, get_legacy_shortcut_path
from .util import log

if os.name == "nt":
    import winreg  # type: ignore[import]


MIGRATION_FLAG = DATA_DIR / "installer_migration_v3.flag"
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"


def _is_installed_app() -> bool:
    try:
        exe = Path(sys.executable).resolve()
        return exe == MANAGER_EXE_PATH.resolve()
    except Exception:
        return False


def _is_old_standalone_candidate(path: Path) -> bool:
    try:
        resolved = path.resolve()
        if not resolved.exists():
            return False
        if resolved.name.lower() != f"{PROJECT_NAME.lower()}.exe":
            return False
        try:
            resolved.relative_to(MANAGER_INSTALL_DIR.resolve())
            return False
        except ValueError:
            return True
    except Exception:
        return False


def _extract_exe_from_command(command: str) -> Path | None:
    text = (command or "").strip()
    if not text:
        return None
    quoted = re.match(r'^\s*"([^"]+\.exe)"', text, re.IGNORECASE)
    if quoted:
        return Path(quoted.group(1))
    unquoted = re.match(r"^\s*(\S+\.exe)", text, re.IGNORECASE)
    if unquoted:
        return Path(unquoted.group(1))
    return None


def _schedule_delete_old_exe(path: Path) -> None:
    if os.name != "nt" or not _is_old_standalone_candidate(path):
        return
    try:
        cmd = (
            f'cmd.exe /C "(timeout /T 5 /NOBREAK >NUL) & '
            f'(for /L %i in (1,1,30) do (del /F /Q "{path}" >NUL 2>&1 && goto :done) & '
            f'timeout /T 1 /NOBREAK >NUL) & :done"'
        )
        subprocess.Popen(
            cmd,
            shell=True,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        log(f"Scheduled old standalone manager executable for deletion: {path}")
    except Exception as exc:
        log(f"Failed to schedule old standalone manager cleanup for {path}: {exc}")


def _migrate_run_key() -> None:
    if os.name != "nt":
        return
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_ALL_ACCESS) as key:
            try:
                value, value_type = winreg.QueryValueEx(key, PROJECT_NAME)
            except FileNotFoundError:
                return

            current = str(value or "")
            old_exe = _extract_exe_from_command(current)
            if not old_exe or not _is_old_standalone_candidate(old_exe):
                return

            start_hidden = "--hidden" in current
            replacement = f'"{MANAGER_EXE_PATH.resolve()}"'
            if start_hidden:
                replacement += " --hidden"
            winreg.SetValueEx(key, PROJECT_NAME, 0, value_type, replacement)
            log(f"Migrated autostart Run key from {old_exe} to {replacement}")
            _schedule_delete_old_exe(old_exe)
    except Exception as exc:
        log(f"Installer migration: failed to migrate Run key: {exc}")


def _shortcut_target(path: Path) -> Path | None:
    if os.name != "nt" or not path.exists():
        return None
    shortcut = str(path).replace("'", "''")
    script = (
        "$w = New-Object -ComObject WScript.Shell; "
        f"$s = $w.CreateShortcut('{shortcut}'); "
        "Write-Output $s.TargetPath"
    )
    try:
        cp = subprocess.run(
            [
                "powershell",
                "-NoLogo",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                script,
            ],
            capture_output=True,
            text=True,
            check=False,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        target = (cp.stdout or "").strip()
        return Path(target) if target else None
    except Exception as exc:
        log(f"Installer migration: failed to read shortcut {path}: {exc}")
        return None


def _migrate_legacy_shortcut() -> None:
    legacy = get_legacy_shortcut_path()
    had_legacy = legacy.exists()
    target = _shortcut_target(legacy)
    if target and _is_old_standalone_candidate(target):
        _schedule_delete_old_exe(target)
    try:
        if legacy.exists():
            legacy.unlink()
            log(f"Removed legacy standalone Start Menu shortcut {legacy}")
    except Exception as exc:
        log(f"Installer migration: failed to remove legacy shortcut {legacy}: {exc}")
    if had_legacy:
        create_start_menu_shortcut()


def run_installer_migration_once() -> None:
    if os.name != "nt" or not _is_installed_app():
        return
    if MIGRATION_FLAG.exists():
        return

    log("Running installer migration for old standalone manager installs.")
    _migrate_run_key()
    _migrate_legacy_shortcut()

    try:
        MIGRATION_FLAG.parent.mkdir(parents=True, exist_ok=True)
        MIGRATION_FLAG.write_text("ok\n", encoding="utf-8")
    except Exception as exc:
        log(f"Installer migration: failed to write migration flag: {exc}")
