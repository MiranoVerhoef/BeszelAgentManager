from __future__ import annotations

import sys

from .bootstrap import ensure_elevated_and_location, ensure_single_instance
from .gui import main as gui_main
from .agent_logs import rotate_agent_logs_and_rename
from .util import log


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


def main() -> None:
    """
    Real entry point used by both:
      - python -m beszel_agent_manager.main
      - the PyInstaller-built BeszelAgentManager.exe (via top-level main.py)
    """
    start_hidden = _parse_start_hidden_flag()
    rotate_agent_logs = _parse_rotate_agent_logs_flag()

    # 1) Make sure we're in the right place and have run once elevated if needed
    ensure_elevated_and_location()

    # If invoked by the scheduled task, just rotate logs and exit.
    if rotate_agent_logs:
        log("Running agent log rotation (CLI mode)")
        rotate_agent_logs_and_rename()
        return

    # 2) Enforce single instance (with “kill old instance?” prompt)
    ensure_single_instance()

    # 3) Start the GUI (which knows what to do with start_hidden)
    log(f"Starting GUI (start_hidden={start_hidden})")
    gui_main(start_hidden=start_hidden)


if __name__ == "__main__":
    main()
