from __future__ import annotations

import json
import os
import re
import ssl
import subprocess
from pathlib import Path
from typing import Dict, List, Optional, Tuple

from .constants import (
    MANAGER_INSTALLER_ASSET_NAME,
    MANAGER_REPO,
    MANAGER_UPDATES_DIR,
    PROJECT_NAME,
    APP_VERSION,
)
from .util import log, github_headers


def _normalize_version(tag: Optional[str]) -> Optional[str]:
    if not tag:
        return None
    tag = str(tag).strip()
    if tag.lower().startswith("v"):
        tag = tag[1:].strip()
    m = re.search(r"\d+\.\d+\.\d+", tag)
    return m.group(0) if m else (tag or None)


def _version_tuple(v: str) -> Tuple[int, int, int]:
    parts = (v.split(".") + ["0", "0", "0"])[:3]
    try:
        return int(parts[0]), int(parts[1]), int(parts[2])
    except Exception:
        return 0, 0, 0


def _powershell_exe() -> str:
    sysroot = os.environ.get("SystemRoot", r"C:\\Windows")
    ps = os.path.join(sysroot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
    return ps if os.path.exists(ps) else "powershell.exe"


def _ps_escape_single(s: str) -> str:
    return s.replace("'", "''")


def _ps_run(command: str, timeout: int = 60) -> subprocess.CompletedProcess:
    ps = _powershell_exe()
    cmd = [ps, "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command]
    creationflags = subprocess.CREATE_NO_WINDOW if hasattr(subprocess, "CREATE_NO_WINDOW") else 0
    return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, creationflags=creationflags)


def _http_get_json(url: str, headers: Optional[Dict[str, str]] = None, timeout: int = 15) -> dict:
    headers = headers or {}
    auth_used = "Authorization" in headers

    try:
        import urllib.request
        import urllib.error

        req = urllib.request.Request(url, headers=headers)
        ctx = ssl.create_default_context()
        with urllib.request.urlopen(req, timeout=timeout, context=ctx) as resp:
            raw = resp.read().decode("utf-8", "replace")
            if auth_used:
                remaining = resp.headers.get("X-RateLimit-Remaining", "?")
                limit = resp.headers.get("X-RateLimit-Limit", "?")
                log(f"GitHub Auth: token used successfully (rate_limit_remaining={remaining}/{limit})")
            return json.loads(raw)
    except urllib.error.HTTPError as exc:
        if auth_used:
            log(f"GitHub Auth: token used but request failed (http={exc.code})")
        if exc.code == 403:
            log(f"GitHub request failed: HTTP {exc.code} (possible rate limit). Falling back to PowerShell.")
        else:
            log(f"GitHub request failed: HTTP {exc.code}. Falling back to PowerShell.")
    except ssl.SSLCertVerificationError as exc:
        if auth_used:
            log("GitHub Auth: token present but SSL verification failed")
        log(f"GitHub request SSL verify failed: {exc}. Falling back to PowerShell.")
    except Exception as exc:
        if auth_used:
            log(f"GitHub Auth: token present but request failed ({exc})")
        log(f"GitHub request failed: {exc}. Falling back to PowerShell.")

    hdr = "@{}"
    if headers:
        parts = [f"'{_ps_escape_single(k)}'='{_ps_escape_single(v)}'" for k, v in headers.items()]
        hdr = "@{" + ";".join(parts) + "}"

    u = _ps_escape_single(url)
    ps_cmd = f"$h={hdr}; (Invoke-WebRequest -UseBasicParsing -Headers $h -Uri '{u}').Content"
    cp = _ps_run(ps_cmd, timeout=timeout)
    if cp.returncode != 0:
        if auth_used:
            log("GitHub Auth: PowerShell fallback failed while using token")
        raise RuntimeError((cp.stderr or cp.stdout or "").strip())
    if auth_used:
        log("GitHub Auth: PowerShell fallback succeeded while using token")
    return json.loads((cp.stdout or "").encode("utf-8", "ignore").decode("utf-8", "replace"))
def _download_file(url: str, dest: Path, headers: Optional[Dict[str, str]] = None, timeout: int = 60) -> None:
    headers = headers or {}
    dest.parent.mkdir(parents=True, exist_ok=True)

    try:
        import urllib.request

        req = urllib.request.Request(url, headers=headers)
        ctx = ssl.create_default_context()
        with urllib.request.urlopen(req, timeout=timeout, context=ctx) as resp, dest.open("wb") as f:
            while True:
                b = resp.read(1024 * 1024)
                if not b:
                    break
                f.write(b)
        return
    except ssl.SSLCertVerificationError as exc:
        log(f"Download SSL verify failed: {exc}. Falling back to PowerShell.")
    except Exception as exc:
        log(f"Download failed: {exc}. Falling back to PowerShell.")

    u = _ps_escape_single(url)
    outp = _ps_escape_single(str(dest))
    ps_cmd = f"Invoke-WebRequest -UseBasicParsing -Uri '{u}' -OutFile '{outp}'"
    cp = _ps_run(ps_cmd, timeout=timeout)
    if cp.returncode != 0:
        raise RuntimeError((cp.stderr or cp.stdout or "").strip())


def fetch_latest_release(*, include_prereleases: bool = False) -> Optional[dict]:
    if not include_prereleases:
        url = f"https://api.github.com/repos/{MANAGER_REPO}/releases/latest"
        headers = github_headers()
        data = _http_get_json(url, headers=headers, timeout=20)

        if data.get("draft") or data.get("prerelease"):
            return None

        tag = str(data.get("tag_name") or "").strip()
        version = _normalize_version(tag)
        body = str(data.get("body") or "")

        asset_url = None
        for a in (data.get("assets") or []):
            name = str(a.get("name") or "")
            if name.lower() == MANAGER_INSTALLER_ASSET_NAME.lower():
                asset_url = a.get("browser_download_url")
                break

        if not version or not asset_url:
            return None

        return {
            "version": version,
            "tag": tag,
            "body": body,
            "download_url": asset_url,
            "asset_name": MANAGER_INSTALLER_ASSET_NAME,
            "published_at": data.get("published_at"),
            "prerelease": bool(data.get("prerelease")),
        }

    rels = fetch_stable_releases(limit=50, include_prereleases=True)
    return rels[0] if rels else None


def fetch_stable_releases(limit: int = 50, *, include_prereleases: bool = False) -> List[dict]:
    url = f"https://api.github.com/repos/{MANAGER_REPO}/releases?per_page={limit}"
    headers = github_headers()
    data = _http_get_json(url, headers=headers, timeout=20)

    releases: List[dict] = []
    for r in data if isinstance(data, list) else []:
        if r.get("draft"):
            continue
        if (not include_prereleases) and r.get("prerelease"):
            continue

        tag = str(r.get("tag_name") or "").strip()
        version = _normalize_version(tag)
        if not version:
            continue

        asset_url = None
        for a in (r.get("assets") or []):
            name = str(a.get("name") or "")
            if name.lower() == MANAGER_INSTALLER_ASSET_NAME.lower():
                asset_url = a.get("browser_download_url")
                break

        if not asset_url:
            continue

        releases.append(
            {
                "version": version,
                "tag": tag,
                "body": str(r.get("body") or ""),
                "download_url": asset_url,
                "asset_name": MANAGER_INSTALLER_ASSET_NAME,
                "published_at": r.get("published_at"),
                "prerelease": bool(r.get("prerelease")),
            }
        )

    releases.sort(key=lambda x: _version_tuple(x["version"]), reverse=True)
    return releases


def is_update_available(current_version: str, latest_version: str) -> bool:
    return _version_tuple(latest_version) > _version_tuple(current_version)


def stage_download(release: dict, force: bool = False) -> Path:
    version = release.get("version") or "unknown"
    download_url = release["download_url"]
    asset_name = str(release.get("asset_name") or MANAGER_INSTALLER_ASSET_NAME).strip()
    if not asset_name:
        asset_name = MANAGER_INSTALLER_ASSET_NAME

    out_dir = MANAGER_UPDATES_DIR / str(version)
    out_dir.mkdir(parents=True, exist_ok=True)
    dest = out_dir / asset_name

    if force and dest.exists():
        try:
            dest.unlink()
        except Exception:
            pass

    log(f"Downloading manager {version} from {download_url}")
    _download_file(download_url, dest, headers={"User-Agent": PROJECT_NAME}, timeout=120)

    try:
        size = dest.stat().st_size
        log(f"Downloaded manager asset size: {size} bytes -> {dest}")
        with dest.open("rb") as f:
            sig = f.read(4)
        if sig[:2] != b"MZ":
            raise RuntimeError(
                f"Downloaded file does not look like a Windows EXE (signature={sig!r}). "
                "Check your GitHub release asset name and the downloaded content."
            )
        if size < 1_000_000:
            raise RuntimeError(f"Downloaded EXE is unexpectedly small ({size} bytes). Aborting update.")
    except Exception as exc:
        log(f"Manager download validation failed: {exc}")
        raise
    return dest


def start_update(
    release: dict,
    args: Optional[List[str]] = None,
    current_pid: Optional[int] = None,
    force: bool = False,
) -> None:
    """Stage release and trigger the installer-based manager update."""
    target = str(release.get('tag_name') or release.get('name') or 'unknown')
    log(f"Manager update requested: {APP_VERSION} -> {target}")

    installer = stage_download(release, force=force)
    is_installer = installer.name.lower() == MANAGER_INSTALLER_ASSET_NAME.lower()
    if not is_installer:
        raise RuntimeError(
            f"Release asset {installer.name} is not supported for in-app updates. "
            f"Expected {MANAGER_INSTALLER_ASSET_NAME}."
        )

    try:
        import ctypes

        params = "/CLOSEAPPLICATIONS /RESTARTAPPLICATIONS"
        rc = ctypes.windll.shell32.ShellExecuteW(None, "runas", str(installer), params, None, 1)
        if rc <= 32:
            raise RuntimeError(f"ShellExecuteW failed with code {rc}")
    except Exception as exc:
        log(f"Failed to start elevated installer update: {exc}")
        raise

    log("Manager installer update started; exiting to allow installer replacement.")
    os._exit(0)
