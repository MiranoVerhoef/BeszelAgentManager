from __future__ import annotations
from beszel_agent_manager.bootstrap import ensure_elevated_and_location


def main() -> None:
    ensure_elevated_and_location()
    from beszel_agent_manager.gui import main as gui_main
    gui_main()


if __name__ == "__main__":
    main()
