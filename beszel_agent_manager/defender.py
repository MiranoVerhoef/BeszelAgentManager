from __future__ import annotations
import os
from pathlib import Path
from .constants import PROJECT_NAME, PROGRAM_FILES
from .util import run, log


def _ps_single_quote(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def _defender_exclusion(path: Path, add: bool) -> tuple[bool, str]:
    if os.name != "nt":
        return True, "Non-Windows platform"

    cmdlet = "Add-MpPreference" if add else "Remove-MpPreference"
    try:
        cp = run(
            [
                "powershell",
                "-NoLogo",
                "-NoProfile",
                "-Command",
                f"{cmdlet} -ExclusionPath {_ps_single_quote(str(path))}",
            ],
            check=False,
        )
        if cp.returncode != 0:
            reason = (cp.stderr or "").strip() or (cp.stdout or "").strip()
            if not reason:
                reason = f"{cmdlet} exited with code {cp.returncode}"
            log(f"{cmdlet} for {path} failed: {reason}")
            return False, reason
        log(f"{cmdlet} for {path} succeeded")
        return True, ""
    except Exception as exc:
        log(f"{cmdlet} for {path} raised: {exc}")
        return False, str(exc)


def ensure_defender_exclusion_for_manager(*, user_consented: bool = False) -> tuple[bool, str]:
    if not user_consented:
        msg = "Windows Defender exclusion was not added because user consent was not provided."
        log(msg)
        return False, msg

    path = Path(PROGRAM_FILES) / PROJECT_NAME
    return _defender_exclusion(path, add=True)


def remove_defender_exclusion_for_manager() -> tuple[bool, str]:
    path = Path(PROGRAM_FILES) / PROJECT_NAME
    return _defender_exclusion(path, add=False)
