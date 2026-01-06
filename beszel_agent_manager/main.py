from __future__ import annotations

import sys
import os
import time
import subprocess
from pathlib import Path

from .bootstrap import ensure_elevated_and_location, ensure_single_instance
from .gui import main as gui_main
from .agent_logs import rotate_agent_logs_and_rename
from .windows_service import restart_service, stop_service, start_service, get_service_status
from .util import log, try_write_event_log
from .constants import AGENT_EXE_PATH, PROJECT_NAME, APP_VERSION


def _parse_update_handshake_flag() -> str | None:
    """Detect and strip the --update-handshake <token> flag from sys.argv.

    This is used by the manager self-updater to verify that the *new* binary
    actually started far enough to run Python code. It should be handled
    before any elevation/single-instance logic.
    """
    argv = list(sys.argv)
    if "--update-handshake" not in argv:
        return None
    try:
        i = argv.index("--update-handshake")
        token = argv[i + 1]
    except Exception:
        # malformed, just strip the flag
        sys.argv = [a for a in argv if a != "--update-handshake"]
        return None

    # Remove flag + token
    new_argv = []
    skip = {i, i + 1}
    for idx, a in enumerate(argv):
        if idx in skip:
            continue
        new_argv.append(a)
    sys.argv = new_argv
    return str(token).strip() or None


def _parse_print_version_flag() -> bool:
    """Detect and strip lightweight version/health flags (no GUI)."""
    argv = sys.argv
    flags = {"--version", "--healthcheck", "--print-version"}
    if not any(a in flags for a in argv):
        return False
    sys.argv = [a for a in argv if a not in flags]
    return True


def _parse_auto_update_agent_flag() -> bool:
    """Detect and strip the --auto-update-agent flag from sys.argv."""
    argv = sys.argv
    if "--auto-update-agent" not in argv:
        return False
    sys.argv = [a for a in argv if a != "--auto-update-agent"]
    return True


def _parse_start_hidden_flag() -> bool:
    """
    Detect and strip the --hidden flag from sys.argv.

    We keep this flag internal so it doesn’t leak to any child processes
    or confuse other argument parsing.
    """
    argv = sys.argv
    if "--hidden" not in argv:
        return False

    # Remove all occurrences of the flag
    sys.argv = [a for a in argv if a != "--hidden"]
    return True


def _parse_rotate_agent_logs_flag() -> bool:
    """Detect and strip the --rotate-agent-logs flag from sys.argv."""
    argv = sys.argv
    if "--rotate-agent-logs" not in argv:
        return False
    sys.argv = [a for a in argv if a != "--rotate-agent-logs"]
    return True


def _parse_restart_agent_service_flag() -> bool:
    """Detect and strip the --restart-agent-service flag from sys.argv."""
    argv = sys.argv
    if "--restart-agent-service" not in argv:
        return False
    sys.argv = [a for a in argv if a != "--restart-agent-service"]
    return True


def main() -> None:
    """
    Real entry point used by both:
      - python -m beszel_agent_manager.main
      - the PyInstaller-built BeszelAgentManager.exe (via top-level main.py)
    """
    # Quick CLI mode for update validation/troubleshooting.
    if _parse_print_version_flag():
        print(APP_VERSION)
        return

    # Updater handshake: if present, write a marker file immediately so the
    # updater can confirm the new binary actually started.
    handshake = _parse_update_handshake_flag()
    if handshake:
        try:
            data_dir = Path(os.getenv("ProgramData", r"C:\\ProgramData")) / PROJECT_NAME
            data_dir.mkdir(parents=True, exist_ok=True)
            marker = data_dir / f"update-handshake-{handshake}.ok"
            marker.write_text(f"version={APP_VERSION}\n", encoding="utf-8")
            log(f"Update handshake written: {marker}")
        except Exception:
            # Never break startup if marker write fails.
            pass

    start_hidden = _parse_start_hidden_flag()
    rotate_agent_logs = _parse_rotate_agent_logs_flag()
    restart_agent_service = _parse_restart_agent_service_flag()
    auto_update_agent = _parse_auto_update_agent_flag()

    # 1) Make sure we're in the right place and have run once elevated if needed
    ensure_elevated_and_location()

    # If invoked by scheduled tasks, run and exit (no GUI / no single-instance prompt).
    if rotate_agent_logs:
        log("Running agent log rotation (CLI mode)")
        rotate_agent_logs_and_rename()
        return

    if restart_agent_service:
        log("Scheduled restart triggered: restarting Beszel Agent service")
        try_write_event_log("Scheduled restart triggered: restarting Beszel Agent service", event_id=2601, level="INFORMATION")
        try:
            restart_service(timeout_seconds=15)
            log("Scheduled restart complete: Beszel Agent service restarted")
            try_write_event_log("Scheduled restart complete: Beszel Agent service restarted", event_id=2602, level="INFORMATION")
        except Exception as exc:
            log(f"Scheduled restart failed: {exc}")
            try_write_event_log(f"Scheduled restart failed: {exc}", event_id=2603, level="ERROR")
        return

    if auto_update_agent:
        # Run the agent's built-in update in a robust, timeout-bound way.
        # This is used by the scheduled auto-update task.
        log("Scheduled agent update triggered")
        try_write_event_log("Scheduled agent update triggered", event_id=2611, level="INFORMATION")

        if not AGENT_EXE_PATH.exists():
            log(f"Scheduled agent update: agent not installed at {AGENT_EXE_PATH}")
            return

        data_dir = Path(os.getenv("ProgramData", r"C:\\ProgramData")) / PROJECT_NAME
        data_dir.mkdir(parents=True, exist_ok=True)
        update_log = data_dir / "update.log"

        forced = False
        try:
            forced = stop_service(timeout_seconds=15)
        except Exception as exc:
            log(f"Scheduled agent update: failed to stop service: {exc}")

        cmd = [str(AGENT_EXE_PATH), "update"]
        try:
            cp = subprocess.run(
                cmd,
                cwd=str(AGENT_EXE_PATH.parent),
                capture_output=True,
                text=True,
                timeout=600,
            )
            out = (cp.stdout or "") + (cp.stderr or "")
            ts = time.strftime("%Y-%m-%d %H:%M:%S")
            try:
                with update_log.open("a", encoding="utf-8", errors="replace") as f:
                    f.write(f"[{ts}] beszel-agent update exit_code={cp.returncode} forced_stop={forced}\n")
                    if out:
                        f.write(out)
                        if not out.endswith("\n"):
                            f.write("\n")
                    f.write("---\n")
            except Exception:
                pass
            log(f"Scheduled agent update finished (exit_code={cp.returncode}, forced_stop={forced})")
        except subprocess.TimeoutExpired:
            log("Scheduled agent update timed out after 600s")
            try_write_event_log("Scheduled agent update timed out after 600s", event_id=2613, level="ERROR")
        except Exception as exc:
            log(f"Scheduled agent update failed: {exc}")
            try_write_event_log(f"Scheduled agent update failed: {exc}", event_id=2613, level="ERROR")
        finally:
            try:
                start_service()
            except Exception:
                pass
            # Wait for RUNNING (max ~20s)
            for _ in range(20):
                if get_service_status() == "RUNNING":
                    break
                time.sleep(1)
            if get_service_status() == "RUNNING":
                log("Scheduled agent update complete: service RUNNING")
                try_write_event_log("Scheduled agent update complete: service RUNNING", event_id=2612, level="INFORMATION")
            else:
                log(f"Scheduled agent update complete: service state={get_service_status()}")
                try_write_event_log(f"Scheduled agent update complete: service state={get_service_status()}", event_id=2614, level="ERROR")
        return

    # 2) Enforce single instance (with “kill old instance?” prompt)
    ensure_single_instance()

    # 3) Start the GUI (which knows what to do with start_hidden)
    log(f"Starting GUI (start_hidden={start_hidden})")
    gui_main(start_hidden=start_hidden)


if __name__ == "__main__":
    main()
