from __future__ import annotations
import os
from pathlib import Path
from .constants import PROJECT_NAME, PROGRAM_FILES
from .util import run, log


def ensure_defender_exclusion_for_manager() -> None:
    """Request a Windows Defender exclusion for the manager install directory.

    This is a best-effort call; if Defender is not present or the command fails,
    the error is logged but not raised.
    """
    if os.name != "nt":
        return
    path = Path(PROGRAM_FILES) / PROJECT_NAME
    try:
        run([
            "powershell",
            "-NoLogo",
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-Command",
            f"Add-MpPreference -ExclusionPath '{str(path)}'"
        ], check=False)
        log(f"Requested Defender exclusion for {path}")
    except Exception as exc:
        log(f"Failed to configure Defender exclusion: {exc}")
