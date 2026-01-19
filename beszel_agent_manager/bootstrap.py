from __future__ import annotations

import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Optional

from .constants import PROJECT_NAME, PROGRAM_FILES, DATA_DIR, LOCK_PATH
from .util import log, ensure_data_dir

ACL_DONE_FLAG = DATA_DIR / "acl_done.flag"



def is_admin() -> bool:
    if os.name != "nt":
        return True

    try:
        import ctypes  # type: ignore[attr-defined]

        return bool(ctypes.windll.shell32.IsUserAnAdmin())  # type: ignore[attr-defined]
    except Exception as exc:
        log(f"is_admin check failed: {exc}")
        return False


def _get_current_exe() -> Optional[Path]:
    if not getattr(sys, "frozen", False):
        return None
    return Path(sys.executable).resolve()


def _run_hidden(cmd, check: bool = False, capture_output: bool = False):
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
        kwargs["creationflags"] = getattr(subprocess, "CREATE_NO_WINDOW", 0)

    try:
        return subprocess.run(cmd, check=check, **kwargs)
    except Exception as exc:
        log(f"Error running command {cmd}: {exc}")
        return None





def _is_pid_running(pid: int) -> bool:
    if os.name != "nt":
        return False
    try:
        r = subprocess.run(
            ["tasklist", "/FI", f"PID eq {pid}"],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        out = (r.stdout or "") + (r.stderr or "")
        return str(pid) in out
    except Exception:
        return False


def _try_close_existing_instance() -> bool:
    if os.name != "nt":
        return False

    pid = None
    try:
        if LOCK_PATH.exists():
            raw = (LOCK_PATH.read_text(encoding="utf-8") or "").strip()
            if raw.isdigit():
                pid = int(raw)
    except Exception:
        pid = None

    if not pid or pid == os.getpid():
        return False

    if not _is_pid_running(pid):
        return False

    try:
        subprocess.run(
            ["taskkill", "/PID", str(pid), "/T"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except Exception:
        pass

    try:
        import time
        time.sleep(1.0)
    except Exception:
        pass

    if not _is_pid_running(pid):
        return True

    try:
        subprocess.run(
            ["taskkill", "/F", "/PID", str(pid), "/T"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except Exception:
        pass

    try:
        import time
        time.sleep(1.0)
    except Exception:
        pass

    return not _is_pid_running(pid)


def _msg_retry_exit(message: str, title: str = PROJECT_NAME) -> bool:
    if os.name != "nt":
        return False
    MB_RETRYCANCEL = 0x00000005
    MB_ICONWARNING = 0x00000030
    try:
        rc = ctypes.windll.user32.MessageBoxW(None, message, title, MB_RETRYCANCEL | MB_ICONWARNING)
        return rc == 4
    except Exception:
        return False
def _adjust_acl(path: Path) -> None:
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
    target_dir = Path(PROGRAM_FILES) / PROJECT_NAME
    target_dir.mkdir(parents=True, exist_ok=True)

    target_exe = target_dir / f"{PROJECT_NAME}.exe"
    if exe_path.resolve() == target_exe.resolve():
        log("Executable is already in Program Files; skipping copy.")
        return target_exe

    _try_close_existing_instance()

    while True:
        try:
            shutil.copy2(exe_path, target_exe)
            log(f"Copied manager executable to {target_exe}")
            return target_exe
        except PermissionError as exc:
            winerr = getattr(exc, "winerror", None)
            if os.name == "nt" and winerr == 32:
                _try_close_existing_instance()

                msg = (
                    "Another BeszelAgentManager is running and preventing the update. "
                    "Close it and try again.\n\n"
                    f"{exc}"
                )
                retry = _msg_retry_exit(msg)
                if retry:
                    continue
                raise
            raise
def _schedule_delete_file(path: Path) -> None:
    if os.name != "nt":
        return
    try:
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

    sys.exit(0)


def ensure_elevated_and_location() -> None:
    ensure_data_dir()

    if os.name != "nt":
        return

    exe_path = _get_current_exe()
    if exe_path is None:
        return

    target_dir = Path(PROGRAM_FILES) / PROJECT_NAME
    target_exe = target_dir / f"{PROJECT_NAME}.exe"

    if exe_path.resolve() != target_exe.resolve():
        if not is_admin():
            _ask_move_and_elevate(exe_path)
            return  # _ask_move_and_elevate exits

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

        try:
            subprocess.Popen([str(new_exe)], shell=False)
            log(f"Started Program Files instance {new_exe} and exiting elevated instance.")
            _schedule_delete_file(exe_path)
        except Exception as exc:
            log(f"Failed to start Program Files instance: {exc}")
        sys.exit(0)

    if is_admin() and not ACL_DONE_FLAG.exists():
        _adjust_acl(target_dir)
        _adjust_acl(DATA_DIR.parent)
        _adjust_acl(DATA_DIR)
        try:
            ACL_DONE_FLAG.parent.mkdir(parents=True, exist_ok=True)
            ACL_DONE_FLAG.write_text("ok", encoding="utf-8")
        except Exception as exc:
            log(f"Failed to write ACL flag: {exc}")



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
            os.kill(pid, 0)
            return True
        except OSError:
            return False


def _ask_kill_existing(pid: int) -> bool:
    if os.name != "nt":
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
        log(f"Stale instance lock detected for PID {existing_pid}, replacing with {os.getpid()}.")

    try:
        LOCK_PATH.write_text(str(os.getpid()), encoding="utf-8")
        log(f"Instance lock set to PID {os.getpid()}")
    except Exception as exc:
        log(f"Failed to write instance lock file {LOCK_PATH}: {exc}")
