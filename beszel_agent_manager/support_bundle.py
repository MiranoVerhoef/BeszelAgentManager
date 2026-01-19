from __future__ import annotations

import datetime
import json
import os
import shutil
import tempfile
import zipfile
from pathlib import Path

from .constants import (
    AGENT_LOG_DIR,
    AUTO_UPDATE_TASK_NAME,
    AGENT_LOG_ROTATE_TASK_NAME,
    CONFIG_PATH,
    LOG_PATH,
    MANAGER_LOG_ARCHIVE_DIR,
    PROJECT_NAME,
    SUPPORT_BUNDLES_DIR,
)
from .util import log, run
from .windows_service import get_service_diagnostics


def _redact_text(text: str) -> str:
    try:
        import re

        t = text or ""
        t = re.sub(r"(?im)^(\s*KEY\s*=).*$", r"\1***redacted***", t)
        t = re.sub(r"(?im)^(\s*TOKEN\s*=).*$", r"\1***redacted***", t)
        t = re.sub(r"(?i)\b(KEY|TOKEN)\s*[:=]\s*[^\s\r\n]+", r"\1=***redacted***", t)
        t = re.sub(r"ssh-(rsa|ed25519)\s+[A-Za-z0-9+/=]+", "ssh-\\1 ***redacted***", t)
        t = re.sub(
            r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
            "***redacted***",
            t,
        )
        return t
    except Exception:
        return text


def _copy_redacted(src: Path, dst: Path) -> None:
    try:
        content = _safe_read_text(src)
        if not content:
            return
        dst.write_text(_redact_text(content), encoding="utf-8")
    except Exception:
        pass


def _safe_read_text(path: Path, max_bytes: int = 5_000_000) -> str:
    try:
        if not path.exists():
            return ""
        data = path.read_bytes()
        if len(data) > max_bytes:
            data = data[-max_bytes:]
        return data.decode("utf-8", errors="replace")
    except Exception:
        return ""


def _run_capture(cmd: list[str], timeout: int = 30) -> str:
    try:
        cp = run(cmd, check=False)
        out = (cp.stdout or "") + (cp.stderr or "")
        return out.strip()
    except Exception as exc:
        return f"Failed to run {cmd}: {exc}"


def _redacted_config_json() -> str:
    try:
        if not CONFIG_PATH.exists():
            return ""
        cfg = json.loads(CONFIG_PATH.read_text(encoding="utf-8", errors="replace"))
        for k in ["key", "token", "KEY", "TOKEN"]:
            if k in cfg and cfg[k]:
                cfg[k] = "***redacted***"
        if isinstance(cfg.get("env_tables"), dict):
            for _name, table in cfg["env_tables"].items():
                if isinstance(table, dict):
                    for kk in list(table.keys()):
                        if kk.upper() in ("KEY", "TOKEN"):
                            table[kk] = "***redacted***"
        return json.dumps(cfg, indent=2)
    except Exception as exc:
        return f"Failed to read/redact config: {exc}"


def create_support_bundle() -> Path:
    SUPPORT_BUNDLES_DIR.mkdir(parents=True, exist_ok=True)

    ts = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out_zip = SUPPORT_BUNDLES_DIR / f"support-bundle-{ts}.zip"

    with tempfile.TemporaryDirectory(prefix=f"{PROJECT_NAME}-support-") as td:
        root = Path(td)
        (root / "logs").mkdir(parents=True, exist_ok=True)

        (root / "config-redacted.json").write_text(_redacted_config_json(), encoding="utf-8")

        try:
            upd = Path(os.getenv("ProgramData", r"C:\\ProgramData")) / PROJECT_NAME / "update.log"
            if upd.exists():
                _copy_redacted(upd, root / "update.log")
        except Exception:
            pass

        try:
            if LOG_PATH.exists():
                _copy_redacted(LOG_PATH, root / "logs" / LOG_PATH.name)
        except Exception:
            pass
        try:
            if MANAGER_LOG_ARCHIVE_DIR.exists():
                for p in sorted(MANAGER_LOG_ARCHIVE_DIR.glob("manager-*.txt")):
                    try:
                        _copy_redacted(p, root / "logs" / p.name)
                    except Exception:
                        pass
        except Exception:
            pass

        try:
            if AGENT_LOG_DIR.exists():
                agent_out = root / "agent_logs"
                agent_out.mkdir(parents=True, exist_ok=True)
                for p in sorted(AGENT_LOG_DIR.iterdir()):
                    if p.is_file():
                        try:
                            _copy_redacted(p, agent_out / p.name)
                        except Exception:
                            pass
        except Exception:
            pass

        try:
            state = Path(os.getenv("ProgramData", r"C:\\ProgramData")) / PROJECT_NAME / "dns-fallback-state.json"
            if state.exists():
                shutil.copy2(state, root / "dns-fallback-state.json")
        except Exception:
            pass

        (root / "service-diagnostics.txt").write_text(get_service_diagnostics(), encoding="utf-8")

        tasks = []
        if AUTO_UPDATE_TASK_NAME:
            tasks.append(AUTO_UPDATE_TASK_NAME)
        if AGENT_LOG_ROTATE_TASK_NAME:
            tasks.append(AGENT_LOG_ROTATE_TASK_NAME)
        if os.name == "nt" and tasks:
            lines: list[str] = []
            for tn in tasks:
                lines.append(f"=== schtasks /Query /TN {tn} ===")
                lines.append(_run_capture(["schtasks", "/Query", "/TN", tn, "/V", "/FO", "LIST"]))
                lines.append("")
            (root / "scheduled-tasks.txt").write_text("\n".join(lines), encoding="utf-8")

        if os.name == "nt":
            fw = _run_capture([
                "netsh",
                "advfirewall",
                "firewall",
                "show",
                "rule",
                f"name=Beszel Agent",
            ])
            (root / "firewall-rule.txt").write_text(fw, encoding="utf-8")

        sys_lines: list[str] = []
        sys_lines.append("=== os.environ (selected) ===")
        for k in ["COMPUTERNAME", "USERNAME", "OS", "PROCESSOR_ARCHITECTURE", "ProgramFiles", "ProgramData"]:
            sys_lines.append(f"{k}={os.environ.get(k, '')}")
        sys_lines.append("")
        if os.name == "nt":
            sys_lines.append("=== systeminfo (trimmed) ===")
            sys_lines.append(_run_capture(["cmd", "/c", "systeminfo"], timeout=60))
            sys_lines.append("")
            sys_lines.append("=== disk space ===")
            sys_lines.append(
                _run_capture(
                    [
                        "powershell.exe",
                        "-NoProfile",
                        "-Command",
                        "Get-CimInstance Win32_LogicalDisk -Filter \"DriveType=3\" | "
                        "Select-Object DeviceID,Size,FreeSpace | Format-Table -AutoSize | Out-String",
                    ],
                    timeout=30,
                )
            )
            sys_lines.append("")
            sys_lines.append("=== powershell version ===")
            sys_lines.append(_run_capture([
                "powershell.exe",
                "-NoProfile",
                "-Command",
                "$PSVersionTable | Out-String",
            ], timeout=30))
        (root / "system-details.txt").write_text("\n".join(sys_lines), encoding="utf-8")

        with zipfile.ZipFile(out_zip, "w", compression=zipfile.ZIP_DEFLATED) as zf:
            for p in root.rglob("*"):
                if p.is_file():
                    zf.write(p, p.relative_to(root).as_posix())

    log(f"Support bundle created: {out_zip}")
    return out_zip
