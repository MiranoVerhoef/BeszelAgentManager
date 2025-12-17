from __future__ import annotations

import datetime
from pathlib import Path

from .constants import LOG_PATH, MANAGER_LOG_ARCHIVE_DIR, MANAGER_LOG_LAST_DATE_PATH


def _read_last_date() -> datetime.date | None:
    try:
        s = MANAGER_LOG_LAST_DATE_PATH.read_text(encoding="utf-8").strip()
        if not s:
            return None
        return datetime.date.fromisoformat(s)
    except Exception:
        return None


def _write_last_date(d: datetime.date) -> None:
    try:
        MANAGER_LOG_LAST_DATE_PATH.parent.mkdir(parents=True, exist_ok=True)
        MANAGER_LOG_LAST_DATE_PATH.write_text(d.isoformat(), encoding="utf-8")
    except Exception:
        pass


def _archive_path_for_date(d: datetime.date) -> Path:
    MANAGER_LOG_ARCHIVE_DIR.mkdir(parents=True, exist_ok=True)
    return MANAGER_LOG_ARCHIVE_DIR / f"manager-{d.isoformat()}.txt"


def rotate_if_needed(force: bool = False) -> Path | None:
    """Rotate manager.log into manager-YYYY-MM-DD.txt when the date changes.

    Returns the archive path if rotation happened.
    """
    today = datetime.date.today()
    last = _read_last_date()

    # First run: establish baseline
    if last is None:
        _write_last_date(today)
        return None

    if not force and last == today:
        return None

    # Rotate existing log to archive for the *previous* recorded date
    try:
        if LOG_PATH.exists() and LOG_PATH.stat().st_size > 0:
            archive = _archive_path_for_date(last)
            # Avoid overwriting: append if already exists
            content = LOG_PATH.read_text(encoding="utf-8", errors="replace")
            if archive.exists():
                archive.write_text(archive.read_text(encoding="utf-8", errors="replace") + content, encoding="utf-8")
            else:
                archive.write_text(content, encoding="utf-8")
            # Truncate current log
            LOG_PATH.write_text("", encoding="utf-8")
        else:
            archive = None
    except Exception:
        archive = None

    _write_last_date(today)
    return archive


def list_manager_log_files() -> list[Path]:
    files: list[Path] = []
    try:
        if LOG_PATH.exists():
            files.append(LOG_PATH)
    except Exception:
        pass

    try:
        if MANAGER_LOG_ARCHIVE_DIR.exists():
            files.extend(sorted(MANAGER_LOG_ARCHIVE_DIR.glob("manager-*.txt"), reverse=True))
    except Exception:
        pass
    return files
