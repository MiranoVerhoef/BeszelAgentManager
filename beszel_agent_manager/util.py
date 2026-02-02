from __future__ import annotations
import datetime
import subprocess
import os
from .constants import DATA_DIR, LOG_PATH, PROJECT_NAME
from .manager_logs import rotate_if_needed

DEBUG_LOGGING = False

_GITHUB_TOKEN: str | None = None


def set_github_token(token: str | None) -> None:
    global _GITHUB_TOKEN
    _GITHUB_TOKEN = (token or "").strip() or None


def github_headers(extra: dict | None = None) -> dict:
    h = {"User-Agent": PROJECT_NAME, "Accept": "application/vnd.github+json"}
    if _GITHUB_TOKEN:
        h["Authorization"] = f"Bearer {_GITHUB_TOKEN}"
    if extra:
        h.update(extra)
    return h


def dpapi_encrypt(plaintext: str) -> str:
    if os.name != "nt":
        return plaintext
    import base64
    import ctypes
    from ctypes import wintypes

    class DATA_BLOB(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_byte))]

    crypt32 = ctypes.windll.crypt32
    kernel32 = ctypes.windll.kernel32

    data = plaintext.encode("utf-8")
    buf = (ctypes.c_byte * len(data)).from_buffer_copy(data)
    in_blob = DATA_BLOB(len(data), ctypes.cast(buf, ctypes.POINTER(ctypes.c_byte)))
    out_blob = DATA_BLOB()

    if not crypt32.CryptProtectData(ctypes.byref(in_blob), None, None, None, None, 0, ctypes.byref(out_blob)):
        raise ctypes.WinError()

    try:
        raw = ctypes.string_at(out_blob.pbData, out_blob.cbData)
        return base64.b64encode(raw).decode("ascii")
    finally:
        kernel32.LocalFree(out_blob.pbData)


def dpapi_decrypt(ciphertext_b64: str) -> str:
    if os.name != "nt":
        return ciphertext_b64
    import base64
    import ctypes
    from ctypes import wintypes

    class DATA_BLOB(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_byte))]

    crypt32 = ctypes.windll.crypt32
    kernel32 = ctypes.windll.kernel32

    raw = base64.b64decode(ciphertext_b64.encode("ascii"))
    buf = (ctypes.c_byte * len(raw)).from_buffer_copy(raw)
    in_blob = DATA_BLOB(len(raw), ctypes.cast(buf, ctypes.POINTER(ctypes.c_byte)))
    out_blob = DATA_BLOB()

    if not crypt32.CryptUnprotectData(ctypes.byref(in_blob), None, None, None, None, 0, ctypes.byref(out_blob)):
        raise ctypes.WinError()

    try:
        data = ctypes.string_at(out_blob.pbData, out_blob.cbData)
        return data.decode("utf-8", errors="ignore")
    finally:
        kernel32.LocalFree(out_blob.pbData)


if os.name == "nt":
    CREATE_NO_WINDOW = subprocess.CREATE_NO_WINDOW
else:
    CREATE_NO_WINDOW = 0


def ensure_data_dir() -> None:
    try:
        DATA_DIR.mkdir(parents=True, exist_ok=True)
    except PermissionError:
        return
    except OSError:
        return

    try:
        LOG_PATH.touch(exist_ok=True)
    except PermissionError:
        pass
    except OSError:
        pass


def log(message: str) -> None:
    ensure_data_dir()
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
        print(line)


def set_debug_logging(enabled: bool) -> None:
    global DEBUG_LOGGING
    DEBUG_LOGGING = bool(enabled)
    log(f"Debug logging set to {DEBUG_LOGGING}")


def run(cmd, check: bool = True) -> subprocess.CompletedProcess:
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
    if os.name != "nt":
        return
    try:
        lvl = (level or "INFORMATION").upper()
        if lvl not in ("INFORMATION", "WARNING", "ERROR"):
            lvl = "INFORMATION"
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
