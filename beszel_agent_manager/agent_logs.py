from __future__ import annotations

import datetime
import re
import time
from pathlib import Path

from .constants import AGENT_LOG_CURRENT_PATH, AGENT_LOG_DIR
from .util import log
from .windows_service import rotate_service_logs


# NSSM rotation typically produces files like:
#   beszel-agent-YYYYMMDDTHHMMSS.log
#   beszel-agent-YYYYMMDDTHHMMSS.mmm.log
_ROTATED_RE = re.compile(
    r"^beszel-agent-(\d{8})T(\d{6})(?:\.(\d{3}))?\.log$",
    re.IGNORECASE,
)


def ensure_agent_log_dir() -> None:
    try:
        AGENT_LOG_DIR.mkdir(parents=True, exist_ok=True)
    except Exception as exc:
        log(f"Failed to create agent log dir {AGENT_LOG_DIR}: {exc}")


def list_agent_log_files() -> list[Path]:
    """Return available agent log files (daily logs + current)."""
    ensure_agent_log_dir()
    files: list[Path] = []
    if AGENT_LOG_CURRENT_PATH.exists():
        files.append(AGENT_LOG_CURRENT_PATH)

    for p in sorted(AGENT_LOG_DIR.glob("*.txt"), reverse=True):
        files.append(p)

    # Also show any raw NSSM rotated files, just in case.
    for p in sorted(AGENT_LOG_DIR.glob("beszel-agent-*.log"), reverse=True):
        if p not in files:
            files.append(p)

    return files


def _date_from_rotated_name(p: Path) -> datetime.date | None:
    m = _ROTATED_RE.match(p.name)
    if not m:
        return None
    ymd = m.group(1)  # YYYYMMDD
    try:
        return datetime.date(int(ymd[0:4]), int(ymd[4:6]), int(ymd[6:8]))
    except Exception:
        return None


def _unique_daily_path(day: datetime.date) -> Path:
    base = AGENT_LOG_DIR / f"{day.isoformat()}.txt"
    if not base.exists():
        return base
    for i in range(2, 50):
        candidate = AGENT_LOG_DIR / f"{day.isoformat()}_{i}.txt"
        if not candidate.exists():
            return candidate
    ts = datetime.datetime.now().strftime("%Y-%m-%d_%H%M%S")
    return AGENT_LOG_DIR / f"{day.isoformat()}_{ts}.txt"


def rotate_agent_logs_and_rename(timeout_seconds: int = 12) -> None:
    """Rotate the agent stdout/stderr log and rename the rotated file to YYYY-MM-DD.txt.

    Uses `nssm rotate <service>` under the hood. Note that NSSM performs the
    rotation after it reads the next line from the managed application, so if
    the agent is completely silent, rotation can be delayed.
    """
    ensure_agent_log_dir()

    before = {p.name for p in AGENT_LOG_DIR.iterdir() if p.is_file()}
    rotate_service_logs()

    deadline = time.time() + max(1, timeout_seconds)
    newest: Path | None = None
    while time.time() < deadline:
        candidates = [
            p
            for p in AGENT_LOG_DIR.glob("beszel-agent-*.log")
            if p.is_file() and p.name not in before
        ]
        if candidates:
            newest = max(candidates, key=lambda p: p.stat().st_mtime)
            break
        time.sleep(0.5)

    if newest is None:
        log(
            "Agent log rotation requested, but no rotated file appeared yet. "
            "This can happen if the agent hasn't produced output since rotation was requested."
        )
        return

    day = _date_from_rotated_name(newest)
    if day is None:
        try:
            day = datetime.date.fromtimestamp(newest.stat().st_mtime)
        except Exception:
            day = datetime.date.today()

    target = _unique_daily_path(day)
    try:
        newest.rename(target)
        log(f"Rotated agent log -> {target}")
    except Exception as exc:
        log(f"Failed to rename rotated agent log {newest} -> {target}: {exc}")
