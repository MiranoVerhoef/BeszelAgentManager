from __future__ import annotations

import os
import sys
import threading
import traceback
import tkinter as tk
from tkinter import ttk, messagebox
from pathlib import Path
import subprocess
import webbrowser
import json
import re

try:
    import pystray  # type: ignore
    from PIL import Image, ImageDraw  # type: ignore
except Exception:  # pragma: no cover - optional dependency
    pystray = None  # type: ignore

from .config import AgentConfig
from .constants import (
    PROJECT_NAME,
    APP_VERSION,
    DEFAULT_LISTEN_PORT,
    AGENT_DIR,
    LOG_PATH,
    LOCK_PATH,
    PROGRAM_FILES,
    DATA_DIR,
    AGENT_LOG_DIR,
    AGENT_LOG_CURRENT_PATH,
)
from .agent_manager import (
    install_or_update_agent_and_service,
    apply_configuration_only,
    get_agent_version,
    update_agent_only,
    check_hub_status,
    fetch_agent_stable_releases,
)
from .windows_service import (
    get_service_status,
    delete_service,
    remove_firewall_rule,
    ensure_firewall_rule,
    start_service,
    stop_service,
    restart_service,
    get_service_diagnostics,
)
from .scheduler import delete_update_task, delete_agent_log_rotate_task
from .agent_logs import list_agent_log_files, rotate_agent_logs_and_rename
from .manager_logs import list_manager_log_files, rotate_if_needed as rotate_manager_logs_if_needed
from .support_bundle import create_support_bundle
from .manager_updater import (
    fetch_latest_release,
    fetch_stable_releases,
    is_update_available,
    start_update,
)
from .util import log, set_debug_logging, run
from .autostart import (
    get_autostart_state,
    set_autostart,
)
from . import shortcut as shortcut_mod
from .defender import (
    ensure_defender_exclusion_for_manager,
    remove_defender_exclusion_for_manager,
)
from .bootstrap import is_admin

# Legacy directory from very early versions (cleanup on uninstall/self-delete)
LEGACY_AGENT_DIR = Path(r"C:\Beszel-Agent")


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _resource_path(relative: str) -> str:
    """
    Return absolute path to a bundled resource when frozen with PyInstaller,
    or the source path when running from source.
    """
    if hasattr(sys, "_MEIPASS"):
        return str(Path(sys._MEIPASS) / relative)
    return str(Path(__file__).resolve().parent.parent / relative)


class ToolTip:
    """
    Simple tooltip helper for ttk widgets.
    """

    def __init__(self, widget, text: str) -> None:
        self.widget = widget
        self.text = text
        self.tip: tk.Toplevel | None = None
        widget.bind("<Enter>", self._show)
        widget.bind("<Leave>", self._hide)

    def _show(self, _event=None):
        if self.tip or not self.text:
            return
        x = self.widget.winfo_rootx() + 20
        y = self.widget.winfo_rooty() + self.widget.winfo_height() + 5

        self.tip = tw = tk.Toplevel(self.widget)
        tw.wm_overrideredirect(True)
        tw.wm_geometry(f"+{x}+{y}")

        label = tk.Label(
            tw,
            text=self.text,
            justify=tk.LEFT,
            relief=tk.SOLID,
            borderwidth=1,
            font=("Segoe UI", 9),
            background="#ffffe0",
        )
        label.pack(ipadx=4, ipady=2)

    def _hide(self, _event=None):
        if self.tip:
            self.tip.destroy()
            self.tip = None


def add_tooltip(widget, text: str):
    if text:
        ToolTip(widget, text)


def _normalize_version(ver: str | None) -> str | None:
    """
    Same semantics as agent_manager._normalize_version, but local to the GUI:
    extract a clean semantic version like 0.17.0 from strings such as:
    - 'v0.17.0'
    - '0.17.0 (windows/amd64)'
    """
    if not ver:
        return None
    ver = ver.strip()
    if ver.lower().startswith("v"):
        ver = ver[1:].strip()
    m = re.search(r"\d+\.\d+\.\d+", ver)
    if m:
        return m.group(0)
    return ver or None


def _fetch_latest_agent_release() -> tuple[str | None, str | None]:
    """
    GUI-side helper: query GitHub for latest Beszel release so we can show
    installed vs latest + "what's changed" in a popup.

    version: normalized version like '0.17.0'
    body:    release body text
    """
    try:
        import urllib.request

        url = "https://api.github.com/repos/henrygd/beszel/releases/latest"
        req = urllib.request.Request(
            url,
            headers={
                "User-Agent": PROJECT_NAME,
                "Accept": "application/vnd.github+json",
            },
        )
        with urllib.request.urlopen(req, timeout=5) as resp:
            data = json.loads(resp.read().decode("utf-8", "replace"))

        tag = str(data.get("tag_name") or "").strip()
        version = _normalize_version(tag)
        if not version:
            return None, None
        body = data.get("body") or ""
        return version, body
    except Exception as exc:
        log(f"GUI: failed to fetch latest Beszel release from GitHub: {exc}")
        return None, None


# ---------------------------------------------------------------------------
# Main application
# ---------------------------------------------------------------------------

class BeszelAgentManagerApp(tk.Tk):
    def __init__(self, start_hidden: bool = False) -> None:
        super().__init__()

        # Start hidden; we'll decide whether to show after config is loaded
        self.withdraw()

        self.title(f"{PROJECT_NAME} v{APP_VERSION}")
        self.geometry("1000x700")
        self.minsize(920, 600)

        self.configure(bg="#f3f3f3")
        try:
            self.iconbitmap(_resource_path("BeszelAgentManager_icon.ico"))
        except Exception:
            pass

        style = ttk.Style()
        try:
            style.theme_use("vista")
        except tk.TclError:
            style.theme_use(style.theme_names()[0])

        base_bg = "#f3f3f3"
        surface_bg = "#ffffff"
        accent = "#2563eb"
        border_color = "#e5e7eb"
        text_primary = "#111827"
        text_muted = "#6b7280"
        self._base_bg = base_bg

        style.configure("TFrame", background=base_bg)
        style.configure("App.TFrame", background=base_bg)
        style.configure("Card.TFrame", background=surface_bg, relief="flat", borderwidth=0)
        style.configure("Header.TFrame", background=base_bg)

        style.configure("TNotebook", background=base_bg, borderwidth=0)
        style.configure("TNotebook.Tab", padding=(12, 6, 12, 6))

        style.configure(
            "Group.TLabelframe",
            background=surface_bg,
            borderwidth=1,
            relief="solid",
        )
        style.configure(
            "Group.TLabelframe.Label",
            background=surface_bg,
            foreground=text_primary,
            font=("Segoe UI", 10, "bold"),
        )

        style.configure("TLabel", background=base_bg, foreground=text_primary, font=("Segoe UI", 9))
        style.configure("Card.TLabel", background=surface_bg, foreground=text_primary, font=("Segoe UI", 9))
        style.configure("Muted.TLabel", background=base_bg, foreground=text_muted, font=("Segoe UI", 9))
        style.configure("Link.TLabel", background=base_bg, foreground=accent, font=("Segoe UI", 9, "underline"))

        style.configure("TButton", padding=(10, 4), relief="flat", borderwidth=1)
        style.map("TButton", relief=[("pressed", "sunken"), ("!pressed", "flat")])
        style.configure("Accent.TButton", padding=(12, 5), foreground=text_primary, borderwidth=1)

        style.configure("TSeparator", background=border_color)

        # State
        self.config_obj = AgentConfig.load()
        self._first_run = not getattr(self.config_obj, "first_run_done", False)
        if self._first_run:
            self.config_obj.first_run_done = True
            self.config_obj.save()

        set_debug_logging(self.config_obj.debug_logging)

        self._task_running = False
        self._tray_icon = None
        self._hub_ping_started = False

        # Build and load variables, then UI
        self._build_vars()
        self._build_ui(accent_color=accent)
        self._update_status()
        self._init_tray()

        # Automatic daily manager log rotation (checks periodically; also rotates on next start)
        self._start_manager_log_rotation_loop()

        # Apply current autostart state
        set_autostart(
            self.var_autostart.get(),
            start_hidden=not self.var_start_visible.get(),
        )

        # Decide visibility
        hide = (
            start_hidden
            and self.var_autostart.get()
            and not self._first_run
            and not self.var_start_visible.get()
        )

        if not hide:
            self.deiconify()

        self._start_hub_ping_loop()

    def _current_relaunch_args(self) -> list[str]:
        """Args to relaunch the manager in the same visible/hidden state."""
        try:
            hidden = not bool(self.winfo_viewable())
        except Exception:
            hidden = False
        return ["--hidden"] if hidden else []

    def _start_manager_log_rotation_loop(self) -> None:
        """Periodically rotate manager.log into daily snapshots."""

        def tick():
            try:
                archive = rotate_manager_logs_if_needed(force=False)
                if archive:
                    log(f"Automatic manager log rotate -> {archive}")
                    # refresh dropdown if user is on logging tab
                    try:
                        self._refresh_manager_log_files_and_view(select_latest=True)
                    except Exception:
                        pass
            except Exception as exc:
                log(f"Automatic manager log rotate failed: {exc}")
            # Every 5 minutes
            try:
                self.after(5 * 60 * 1000, tick)
            except Exception:
                pass

        try:
            self.after(1000, tick)
        except Exception:
            pass

    # ------------------------------------------------------------------ Vars / autosave

    def _build_vars(self):
        c = self.config_obj

        # Core connection settings
        self.var_key = tk.StringVar(value=c.key)
        self.var_token = tk.StringVar(value=c.token)
        self.var_hub_url = tk.StringVar(value=c.hub_url)
        # LISTEN is optional; when blank the agent uses its default (45876)
        self.var_listen = tk.StringVar(value=str(c.listen) if c.listen else "")

        # Advanced env vars
        self.var_data_dir = tk.StringVar(value=c.data_dir)
        self.var_docker_host = tk.StringVar(value=c.docker_host)
        self.var_exclude_containers = tk.StringVar(value=c.exclude_containers)
        self.var_exclude_smart = tk.StringVar(value=c.exclude_smart)
        self.var_extra_filesystems = tk.StringVar(value=c.extra_filesystems)
        self.var_filesystem = tk.StringVar(value=c.filesystem)
        self.var_intel_gpu_device = tk.StringVar(value=c.intel_gpu_device)
        self.var_key_file = tk.StringVar(value=c.key_file)
        self.var_token_file = tk.StringVar(value=c.token_file)
        self.var_lhm = tk.StringVar(value=c.lhm)
        self.var_log_level = tk.StringVar(value=c.log_level)
        self.var_mem_calc = tk.StringVar(value=c.mem_calc)
        self.var_network = tk.StringVar(value=c.network)
        self.var_nics = tk.StringVar(value=c.nics)
        self.var_sensors = tk.StringVar(value=c.sensors)
        self.var_primary_sensor = tk.StringVar(value=c.primary_sensor)
        self.var_sys_sensors = tk.StringVar(value=c.sys_sensors)
        self.var_service_patterns = tk.StringVar(value=c.service_patterns)
        self.var_smart_devices = tk.StringVar(value=c.smart_devices)
        self.var_system_name = tk.StringVar(value=c.system_name)
        self.var_skip_gpu = tk.StringVar(value=c.skip_gpu)

        # New env vars (v0.17.0)
        self.var_disk_usage_cache = tk.StringVar(
            value=getattr(c, "disk_usage_cache", "")
        )
        self.var_skip_systemd = tk.StringVar(
            value=getattr(c, "skip_systemd", "")
        )

        # Auto update
        self.var_auto_update = tk.BooleanVar(value=c.auto_update_enabled)
        self.var_update_interval = tk.IntVar(value=c.update_interval_days or 1)

        # Logging & startup
        self.var_debug_logging = tk.BooleanVar(value=c.debug_logging)

        # Read back actual Run-key
        enabled, start_hidden_flag = get_autostart_state()
        self.var_autostart = tk.BooleanVar(value=enabled)
        self.var_start_visible = tk.BooleanVar(
            value=enabled and not start_hidden_flag
        )

        # Env table definitions
        self.env_definitions = [
            ("DATA_DIR", self.var_data_dir, "Custom data directory used by the agent (DATA_DIR)."),
            ("DOCKER_HOST", self.var_docker_host, "Docker host to connect to."),
            ("EXCLUDE_CONTAINERS", self.var_exclude_containers, "Containers to exclude."),
            ("EXCLUDE_SMART", self.var_exclude_smart, "Disks to exclude from SMART."),
            ("EXTRA_FILESYSTEMS", self.var_extra_filesystems, "Extra filesystems/paths."),
            ("FILESYSTEM", self.var_filesystem, "Root filesystem override."),
            ("INTEL_GPU_DEVICE", self.var_intel_gpu_device, "Device path for intel_gpu_top."),
            ("KEY_FILE", self.var_key_file, "File containing the KEY."),
            ("TOKEN_FILE", self.var_token_file, "File containing the TOKEN."),
            ("LHM", self.var_lhm, "Enable LibreHardwareMonitor integration."),
            ("LOG_LEVEL", self.var_log_level, "Agent log level."),
            ("MEM_CALC", self.var_mem_calc, "Memory calculation mode."),
            ("NETWORK", self.var_network, "Network mode."),
            ("NICS", self.var_nics, "Network interfaces to include."),
            ("SENSORS", self.var_sensors, "Sensors to read."),
            ("PRIMARY_SENSOR", self.var_primary_sensor, "Primary temperature sensor."),
            ("SYS_SENSORS", self.var_sys_sensors, "System sensors path."),
            ("SERVICE_PATTERNS", self.var_service_patterns, "Service patterns to monitor."),
            ("SMART_DEVICES", self.var_smart_devices, "SMART devices list."),
            ("SYSTEM_NAME", self.var_system_name, "Override host name."),
            ("SKIP_GPU", self.var_skip_gpu, "Skip GPU stats collection."),
            ("DISK_USAGE_CACHE", self.var_disk_usage_cache, "Directory used for disk usage cache."),
            ("SKIP_SYSTEMD", self.var_skip_systemd, "Skip systemd integration (set to '1' to disable)."),
        ]

        self.active_env_names: list[str] = [
            name for (name, var, _tip) in self.env_definitions if var.get().strip()
        ]

        self.var_env_enabled = tk.BooleanVar(value=self._any_env_nonempty())

        self._env_entries: list[ttk.Entry] = []
        self._env_delete_buttons: list[ttk.Button] = []
        self._env_edit_buttons: list[ttk.Button] = []
        self._env_editing: set[str] = set()

        # Agent log viewer state
        self.var_agent_log_choice = tk.StringVar(value="")
        self._agent_log_paths: list[Path] = []

        autosave_vars = (
            self.var_key,
            self.var_token,
            self.var_hub_url,
            self.var_listen,
            self.var_data_dir,
            self.var_docker_host,
            self.var_exclude_containers,
            self.var_exclude_smart,
            self.var_extra_filesystems,
            self.var_filesystem,
            self.var_intel_gpu_device,
            self.var_key_file,
            self.var_token_file,
            self.var_lhm,
            self.var_log_level,
            self.var_mem_calc,
            self.var_network,
            self.var_nics,
            self.var_sensors,
            self.var_primary_sensor,
            self.var_sys_sensors,
            self.var_service_patterns,
            self.var_smart_devices,
            self.var_system_name,
            self.var_skip_gpu,
            self.var_disk_usage_cache,
            self.var_skip_systemd,
            self.var_auto_update,
            self.var_update_interval,
            self.var_debug_logging,
            self.var_autostart,
            self.var_start_visible,
        )
        for v in autosave_vars:
            v.trace_add("write", self._on_var_changed)

        # Agent log view state
        self.var_agent_log_file = tk.StringVar(value="")

    def _any_env_nonempty(self) -> bool:
        return any(
            v.get().strip()
            for v in (
                self.var_data_dir,
                self.var_docker_host,
                self.var_exclude_containers,
                self.var_exclude_smart,
                self.var_extra_filesystems,
                self.var_filesystem,
                self.var_intel_gpu_device,
                self.var_key_file,
                self.var_token_file,
                self.var_lhm,
                self.var_log_level,
                self.var_mem_calc,
                self.var_network,
                self.var_nics,
                self.var_sensors,
                self.var_primary_sensor,
                self.var_sys_sensors,
                self.var_service_patterns,
                self.var_smart_devices,
                self.var_system_name,
                self.var_skip_gpu,
                self.var_disk_usage_cache,
                self.var_skip_systemd,
            )
        )

    def _on_var_changed(self, *_args):
        self._autosave_config()

    def _autosave_config(self):
        try:
            cfg = self._build_config()
            cfg.save()
            self.config_obj = cfg

            try:
                set_autostart(
                    self.var_autostart.get(),
                    start_hidden=not self.var_start_visible.get(),
                )
            except Exception as exc:
                log(f"Failed to update autostart from autosave: {exc}")

            self.label_config_saved.config(text="Config saved")
            self.after(2000, lambda: self.label_config_saved.config(text=""))
        except Exception as exc:
            log(f"Autosave failed: {exc}")

    # ------------------------------------------------------------------ UI construction

    def _build_ui(self, accent_color: str):
        self.columnconfigure(0, weight=1)
        self.rowconfigure(0, weight=1)

        outer = ttk.Frame(self, padding=(16, 16, 16, 10), style="App.TFrame")
        outer.grid(row=0, column=0, sticky="nsew")
        outer.columnconfigure(0, weight=1)
        outer.rowconfigure(1, weight=1)

        # Header
        header = ttk.Frame(outer, style="Header.TFrame", padding=(0, 0, 0, 10))
        header.grid(row=0, column=0, sticky="ew")
        header.columnconfigure(0, weight=1)

        title_lbl = ttk.Label(
            header,
            text=PROJECT_NAME,
            font=("Segoe UI", 17, "bold"),
        )
        subtitle_lbl = ttk.Label(
            header,
            text="Beszel agent installer & manager",
            style="Muted.TLabel",
        )

        right_header = ttk.Frame(header, style="Header.TFrame")
        right_header.grid(row=0, column=1, rowspan=2, sticky="e")

        chip_frame = tk.Frame(right_header, bg=accent_color)
        chip_inner = tk.Label(
            chip_frame,
            text=f"v{APP_VERSION}",
            font=("Segoe UI", 9, "bold"),
            fg="#ffffff",
            bg=accent_color,
            padx=10,
            pady=3,
        )
        chip_inner.pack()
        chip_frame.pack(side=tk.TOP, anchor="e")

        title_lbl.grid(row=0, column=0, sticky="w")
        subtitle_lbl.grid(row=1, column=0, sticky="w", pady=(0, 2))

        # Main card
        card = ttk.Frame(outer, style="Card.TFrame")
        card.grid(row=1, column=0, sticky="nsew")
        card.columnconfigure(0, weight=1)
        card.rowconfigure(0, weight=1)

        inner = ttk.Frame(card, style="Card.TFrame", padding=12)
        inner.grid(row=0, column=0, sticky="nsew")
        inner.columnconfigure(0, weight=1)
        inner.rowconfigure(0, weight=1)

        notebook = ttk.Notebook(inner)
        # Keep a reference so we can determine the active tab for auto-refresh.
        self.notebook = notebook
        notebook.grid(row=0, column=0, sticky="nsew")

        # ------------------------------------------------------------------ Connection tab
        conn = ttk.Frame(notebook, padding=10, style="Card.TFrame")
        conn.columnconfigure(1, weight=1)
        conn.columnconfigure(2, weight=0)
        conn.columnconfigure(3, weight=0)
        notebook.add(conn, text="Connection")

        self._make_locked_entry_with_dialog(
            conn,
            row=0,
            label="Key",
            var=self.var_key,
            tooltip="Public key from Beszel hub (KEY).",
            is_int=False,
        )
        self._make_locked_entry_with_dialog(
            conn,
            row=1,
            label="Token",
            var=self.var_token,
            tooltip="Optional token (TOKEN).",
            is_int=False,
        )
        self._make_locked_entry_with_dialog(
            conn,
            row=2,
            label="Hub URL",
            var=self.var_hub_url,
            tooltip="Monitoring / hub URL (HUB_URL).",
            is_int=False,
        )
        self._make_locked_entry_with_dialog(
            conn,
            row=3,
            label="Listen (port)",
            var=self.var_listen,
            tooltip="Optional agent listen port (LISTEN). Leave blank to use the agent default (45876).",
            is_int=True,
            allow_empty=True,
        )

        # Dynamic LISTEN firewall toggle
        self.btn_listen_toggle = ttk.Button(
            conn,
            text="Enable",
            command=self._on_toggle_listen_firewall,
            width=9,
        )
        self.btn_listen_toggle.grid(row=3, column=3, sticky="w", padx=(4, 0), pady=2)
        add_tooltip(
            self.btn_listen_toggle,
            "Enable: set a port (if empty), apply settings, and add a Windows Firewall allow rule.\n"
            "Disable: clear LISTEN, apply settings, and remove the firewall rule.",
        )

        # Keep button text in sync with the LISTEN field
        try:
            self.var_listen.trace_add("write", lambda *_: self._update_listen_toggle_button())
        except Exception:
            pass
        self._update_listen_toggle_button()

        auto = ttk.LabelFrame(
            conn,
            text="Automatic updates",
            padding=10,
            style="Group.TLabelframe",
        )
        auto.grid(row=4, column=0, sticky="nsew", pady=(12, 0), padx=(0, 6))
        auto.columnconfigure(1, weight=1)

        chk_auto = ttk.Checkbutton(
            auto,
            text="Enable automatic updates",
            variable=self.var_auto_update,
        )
        chk_auto.grid(row=0, column=0, columnspan=2, sticky="w")
        add_tooltip(
            chk_auto,
            "Create a scheduled task that runs 'beszel-agent update' every N days.",
        )

        ttk.Label(auto, text="Interval (days):", style="Card.TLabel").grid(
            row=1, column=0, sticky="w", pady=(6, 0)
        )
        spin = ttk.Spinbox(
            auto, from_=1, to=90, textvariable=self.var_update_interval, width=6
        )
        spin.grid(row=1, column=1, sticky="w", pady=(6, 0))

        startup = ttk.LabelFrame(
            conn,
            text="Manager startup",
            padding=10,
            style="Group.TLabelframe",
        )
        startup.grid(row=4, column=1, sticky="nsew", pady=(12, 0), padx=(6, 0))

        chk_start = ttk.Checkbutton(
            startup,
            text="Start BeszelAgentManager with Windows",
            variable=self.var_autostart,
        )
        chk_start.grid(row=0, column=0, sticky="w")
        add_tooltip(
            chk_start,
            "Create/remove the autostart (Run key) entry for this user.",
        )

        chk_show = ttk.Checkbutton(
            startup,
            text="Show window when starting with Windows",
            variable=self.var_start_visible,
        )
        chk_show.grid(row=1, column=0, sticky="w", pady=(4, 0))
        add_tooltip(
            chk_show,
            "When disabled, the manager will start in the tray only on logon.\n"
            "When enabled, the main window will open on logon.",
        )

        svc = ttk.LabelFrame(
            conn,
            text="Service control",
            padding=10,
            style="Group.TLabelframe",
        )
        svc.grid(row=5, column=0, columnspan=2, sticky="ew", pady=(12, 0))
        btn_s_start = ttk.Button(svc, text="Start service", command=self._on_start_service)
        btn_s_start.grid(row=0, column=0, padx=(0, 6), pady=2, sticky="w")
        add_tooltip(btn_s_start, "Start the Beszel Windows service.")

        btn_s_stop = ttk.Button(svc, text="Stop service", command=self._on_stop_service)
        btn_s_stop.grid(row=0, column=1, padx=(0, 6), pady=2, sticky="w")
        add_tooltip(btn_s_stop, "Stop the Beszel Windows service.")

        btn_s_restart = ttk.Button(
            svc, text="Restart service", command=self._on_restart_service
        )
        btn_s_restart.grid(row=0, column=2, padx=(0, 6), pady=2, sticky="w")
        add_tooltip(btn_s_restart, "Restart the Beszel Windows service.")

        # ------------------------------------------------------------------ Environment Tables tab
        env_tab = ttk.Frame(notebook, padding=10, style="Card.TFrame")
        env_tab.columnconfigure(1, weight=1)
        env_tab.rowconfigure(2, weight=1)
        notebook.add(env_tab, text="Environment Tables")

        chk_env = ttk.Checkbutton(
            env_tab,
            text="Enable environment variables",
            variable=self.var_env_enabled,
            command=self._on_env_toggle,
        )
        chk_env.grid(row=0, column=0, columnspan=3, sticky="w")
        add_tooltip(
            chk_env,
            "Enable or disable the advanced environment variable configuration.",
        )

        ttk.Label(env_tab, text="Add environment:", style="Card.TLabel").grid(
            row=1, column=0, sticky="w", pady=(8, 2)
        )

        self.var_env_choice = tk.StringVar()
        self.env_combo = ttk.Combobox(
            env_tab,
            textvariable=self.var_env_choice,
            state="readonly",
        )
        self.env_combo.grid(row=1, column=1, sticky="ew", pady=(8, 2))

        self.btn_env_add = ttk.Button(
            env_tab,
            text="Add",
            command=self._on_env_add,
            width=8,
        )
        self.btn_env_add.grid(row=1, column=2, sticky="w", pady=(8, 2), padx=(4, 0))

        self.env_rows_frame = ttk.Frame(
            env_tab, style="Card.TFrame", padding=(6, 6, 0, 0)
        )
        self.env_rows_frame.grid(row=2, column=0, columnspan=3, sticky="nsew", pady=(8, 0))
        self.env_rows_frame.columnconfigure(1, weight=1)

        self._rebuild_env_rows()

        # ------------------------------------------------------------------ Logging tab
        log_tab = ttk.Frame(notebook, padding=10, style="Card.TFrame")
        log_tab.columnconfigure(0, weight=1)
        log_tab.columnconfigure(1, weight=0)
        # Keep the controls row visible; only the log text area should expand.
        log_tab.rowconfigure(4, weight=1)
        notebook.add(log_tab, text="Logging")

        # Top actions row: debug toggle (left) + support bundle (right)
        top_actions = ttk.Frame(log_tab, style="Card.TFrame")
        top_actions.grid(row=0, column=0, columnspan=2, sticky="ew")
        top_actions.columnconfigure(0, weight=1)

        chk_debug = ttk.Checkbutton(
            top_actions, text="Enable debug logging", variable=self.var_debug_logging
        )
        chk_debug.grid(row=0, column=0, sticky="w")
        add_tooltip(
            chk_debug,
            "When enabled, detailed operations are written to manager.log.",
        )

        ttk.Button(
            top_actions,
            text="Export Support Bundle",
            command=self._on_export_support_bundle,
        ).grid(row=0, column=1, sticky="e")

        ttk.Separator(log_tab, orient="horizontal").grid(
            row=1, column=0, columnspan=2, sticky="ew", pady=8
        )

        ttk.Label(
            log_tab,
            text=f"Manager log folder: {DATA_DIR}",
            style="Card.TLabel",
        ).grid(row=2, column=0, columnspan=2, sticky="w")

        # Manager log controls (dropdown + rotate/refresh + support bundle)
        mgr_controls = ttk.Frame(log_tab, style="Card.TFrame")
        mgr_controls.grid(row=3, column=0, columnspan=2, sticky="ew", pady=(8, 0))
        # Make sure controls remain visible even on narrower windows.
        mgr_controls.columnconfigure(1, weight=1)

        ttk.Label(mgr_controls, text="File:", style="Card.TLabel").grid(row=0, column=0, sticky="w")
        self.var_manager_log_file = tk.StringVar()
        self.manager_log_combo = ttk.Combobox(
            mgr_controls,
            textvariable=self.var_manager_log_file,
            state="readonly",
        )
        self.manager_log_combo.grid(row=0, column=1, sticky="ew", padx=(6, 6))
        self.manager_log_combo.bind("<<ComboboxSelected>>", lambda _e: self._refresh_log_view())

        # Row 0: file + refresh/rotate
        ttk.Button(mgr_controls, text="Refresh", command=self._refresh_manager_log_files_and_view).grid(
            row=0, column=2, sticky="e"
        )
        ttk.Button(mgr_controls, text="Rotate now", command=self._on_rotate_manager_logs).grid(
            row=0, column=3, sticky="e", padx=(6, 0)
        )

        # Row 1: secondary actions (kept visible on smaller widths)
        mgr_controls.rowconfigure(1, weight=0)
        ttk.Button(mgr_controls, text="Open folder", command=lambda: self._open_path(str(DATA_DIR))).grid(
            row=1, column=2, sticky="e", pady=(6, 0)
        )
        # (Export Support Bundle button is placed on the top row next to debug checkbox.)

        log_frame = ttk.Frame(log_tab, style="Card.TFrame", padding=(4, 4, 4, 4))
        log_frame.grid(row=4, column=0, columnspan=2, sticky="nsew", pady=(8, 0))
        log_frame.columnconfigure(0, weight=1)
        log_frame.rowconfigure(0, weight=1)

        self.log_text = tk.Text(
            log_frame,
            wrap="none",
            font=("Consolas", 9),
            state="disabled",
            bg="#ffffff",
        )
        log_scroll = ttk.Scrollbar(
            log_frame, orient="vertical", command=self.log_text.yview
        )
        self.log_text.configure(yscrollcommand=log_scroll.set)

        self.log_text.grid(row=0, column=0, sticky="nsew")
        log_scroll.grid(row=0, column=1, sticky="ns")

        self._refresh_manager_log_files_and_view(select_latest=True)

        # ------------------------------------------------------------------ Agent Logging tab
        agent_log_tab = ttk.Frame(notebook, padding=10, style="Card.TFrame")
        agent_log_tab.columnconfigure(0, weight=1)
        agent_log_tab.rowconfigure(4, weight=1)
        notebook.add(agent_log_tab, text="Agent Logging")

        ttk.Label(
            agent_log_tab,
            text=f"Agent log folder: {AGENT_LOG_DIR}",
            style="Card.TLabel",
        ).grid(row=0, column=0, sticky="w")

        controls = ttk.Frame(agent_log_tab, style="Card.TFrame")
        controls.grid(row=1, column=0, sticky="ew", pady=(8, 0))
        controls.columnconfigure(1, weight=1)

        ttk.Label(controls, text="File:", style="Card.TLabel").grid(row=0, column=0, sticky="w")

        self.var_agent_log_file = tk.StringVar()
        self.agent_log_combo = ttk.Combobox(
            controls,
            textvariable=self.var_agent_log_file,
            state="readonly",
        )
        self.agent_log_combo.grid(row=0, column=1, sticky="ew", padx=(6, 6))
        self.agent_log_combo.bind("<<ComboboxSelected>>", lambda _e: self._refresh_agent_log_view())

        btn_refresh_agent_logs = ttk.Button(
            controls, text="Refresh", command=self._refresh_agent_log_files_and_view
        )
        btn_refresh_agent_logs.grid(row=0, column=2, sticky="e")

        btn_rotate_agent_logs = ttk.Button(
            controls, text="Rotate now", command=self._on_rotate_agent_logs
        )
        btn_rotate_agent_logs.grid(row=0, column=3, sticky="e", padx=(6, 0))
        add_tooltip(
            btn_rotate_agent_logs,
            "Triggers on-demand rotation and moves the rotated file to YYYY-MM-DD.txt (requires Admin).",
        )

        ttk.Separator(agent_log_tab, orient="horizontal").grid(row=2, column=0, sticky="ew", pady=8)

        ttk.Label(
            agent_log_tab,
            text=f"Current capture file: {AGENT_LOG_CURRENT_PATH}",
            style="Card.TLabel",
        ).grid(row=3, column=0, sticky="w")

        agent_log_frame = ttk.Frame(agent_log_tab, style="Card.TFrame", padding=(4, 4, 4, 4))
        agent_log_frame.grid(row=4, column=0, sticky="nsew", pady=(8, 0))
        agent_log_frame.columnconfigure(0, weight=1)
        agent_log_frame.rowconfigure(0, weight=1)

        self.agent_log_text = tk.Text(
            agent_log_frame,
            wrap="none",
            font=("Consolas", 9),
            state="disabled",
            bg="#ffffff",
        )
        agent_log_scroll = ttk.Scrollbar(
            agent_log_frame, orient="vertical", command=self.agent_log_text.yview
        )
        self.agent_log_text.configure(yscrollcommand=agent_log_scroll.set)

        self.agent_log_text.grid(row=0, column=0, sticky="nsew")
        agent_log_scroll.grid(row=0, column=1, sticky="ns")

        self._agent_log_files_map: dict[str, Path] = {}
        self._refresh_agent_log_files_and_view(select_latest=True)

        # ------------------------------------------------------------------ Bottom section
        outer.rowconfigure(2, weight=0)
        outer.rowconfigure(3, weight=0)

        bottom = ttk.Frame(outer, style="App.TFrame")
        bottom.grid(row=2, column=0, sticky="ew", pady=(10, 4))
        # Column 0 acts as a flexible spacer so the action buttons align nicely.
        bottom.columnconfigure(0, weight=1)
        for i in range(1, 12):
            bottom.columnconfigure(i, weight=0)

        status_frame = ttk.Frame(outer, style="App.TFrame")
        status_frame.grid(row=3, column=0, sticky="ew")
        status_frame.columnconfigure(0, weight=1)
        status_frame.columnconfigure(1, weight=0)
        status_frame.columnconfigure(2, weight=0)

        self.label_app_version = ttk.Label(
            status_frame,
            text=f"{PROJECT_NAME} v{APP_VERSION}",
            style="Link.TLabel",
        )
        self.label_app_version.grid(row=0, column=0, sticky="w")
        self.label_app_version.bind("<Button-1>", self._on_version_clicked)
        self.label_app_version.bind(
            "<Enter>", lambda e: self.label_app_version.configure(cursor="hand2")
        )
        self.label_app_version.bind(
            "<Leave>", lambda e: self.label_app_version.configure(cursor="")
        )

        self.label_status = ttk.Label(status_frame, text="Service status: Unknown")
        self.label_status.grid(row=1, column=0, sticky="w")

        self.label_version = ttk.Label(
            status_frame, text="Agent version: Not installed"
        )
        self.label_version.grid(row=2, column=0, sticky="w")

        hub_row = ttk.Frame(status_frame, style="App.TFrame")
        hub_row.grid(row=3, column=0, sticky="w")

        self.hub_status_canvas = tk.Canvas(
            hub_row,
            width=10,
            height=10,
            highlightthickness=0,
            bd=0,
            bg=self._base_bg,
        )
        self.hub_status_canvas.grid(row=0, column=0, padx=(0, 4))
        self._hub_indicator_circle = self.hub_status_canvas.create_oval(
            1, 1, 9, 9, fill="#9ca3af", outline="#9ca3af"
        )

        self.label_hub_status = ttk.Label(
            hub_row,
            text="Hub: Unknown",
            style="Link.TLabel",
        )
        self.label_hub_status.grid(row=0, column=1, sticky="w")
        self.label_hub_status.bind("<Button-1>", self._on_hub_clicked)
        self.label_hub_status.bind(
            "<Enter>", lambda e: self.label_hub_status.configure(cursor="hand2")
        )
        self.label_hub_status.bind(
            "<Leave>", lambda e: self.label_hub_status.configure(cursor="")
        )

        self.label_config_saved = ttk.Label(
            status_frame, text="", foreground="green"
        )
        self.label_config_saved.grid(row=4, column=0, sticky="w")

        self.progress = ttk.Progressbar(
            status_frame, mode="indeterminate", length=180
        )
        self.progress.grid(row=0, column=1, rowspan=5, sticky="e")
        self.progress.grid_remove()

        # Bottom-right hyperlinks (in the status bar)
        link_about = ttk.Label(status_frame, text="About", style="Link.TLabel")
        link_about.grid(row=0, column=1, padx=(4, 6), sticky="e")
        link_about.bind(
            "<Button-1>",
            lambda e: self._open_url("https://github.com/MiranoVerhoef/BeszelAgentManager"),
        )
        link_about.bind("<Enter>", lambda e: link_about.configure(cursor="hand2"))
        link_about.bind("<Leave>", lambda e: link_about.configure(cursor=""))

        link_about_beszel = ttk.Label(status_frame, text="About Beszel", style="Link.TLabel")
        link_about_beszel.grid(row=0, column=2, padx=(0, 4), sticky="e")
        link_about_beszel.bind("<Button-1>", lambda e: self._open_url("https://beszel.dev"))
        link_about_beszel.bind("<Enter>", lambda e: link_about_beszel.configure(cursor="hand2"))
        link_about_beszel.bind("<Leave>", lambda e: link_about_beszel.configure(cursor=""))

        # Bottom action buttons (uniform width)
        BTN_W = 24

        btn_install = ttk.Button(bottom, text="Install agent", width=BTN_W, command=self._on_install)
        btn_install.grid(row=0, column=1, padx=4, pady=4, sticky="e")

        # Agent: quick update button (row 0) + manage version button (row 1)
        btn_update_agent = ttk.Button(bottom, text="Update agent", width=BTN_W, command=self._on_update_agent)
        btn_update_agent.grid(row=0, column=2, padx=4, pady=4, sticky="e")
        add_tooltip(btn_update_agent, "Check GitHub and update the Beszel agent if a newer version is available.")

        btn_manage_agent = ttk.Button(bottom, text="Manage Agent Version...", width=BTN_W, command=self._on_manage_agent_version)
        btn_manage_agent.grid(row=1, column=2, padx=4, pady=(0, 4), sticky="e")
        add_tooltip(btn_manage_agent, "Pick a version to install/rollback, or force reinstall the latest agent.")

        # Manager: quick update button (row 0) + manage version button (row 1)
        btn_update_manager = ttk.Button(bottom, text="Update manager", width=BTN_W, command=self._on_update_manager)
        btn_update_manager.grid(row=0, column=3, padx=4, pady=4, sticky="e")
        add_tooltip(btn_update_manager, "Check GitHub and update BeszelAgentManager if a newer version is available.")

        btn_manage_manager = ttk.Button(bottom, text="Manage Manager Version...", width=BTN_W, command=self._on_manage_manager_version)
        btn_manage_manager.grid(row=1, column=3, padx=4, pady=(0, 4), sticky="e")
        add_tooltip(btn_manage_manager, "Pick a version to install/rollback, or force reinstall the latest manager.")

        btn_apply = ttk.Button(bottom, text="Apply settings", width=BTN_W, command=self._on_apply)
        btn_apply.grid(row=0, column=4, padx=4, pady=4, sticky="e")

        btn_uninstall = ttk.Button(bottom, text="Uninstall agent", width=BTN_W, command=self._on_uninstall)
        btn_uninstall.grid(row=0, column=5, padx=4, pady=4, sticky="e")

        self.protocol("WM_DELETE_WINDOW", self._on_close)

        # Auto-refresh log viewers every 10 seconds (only refresh the active tab).
        self._auto_log_refresh_ticks = 0
        self.after(10_000, self._auto_refresh_logs)


    def _auto_refresh_logs(self):
        """Auto-refresh log viewers every 10 seconds.

        To avoid heavy I/O, we refresh only the currently visible tab, and refresh the
        file dropdown lists roughly every minute.
        """
        try:
            self._auto_log_refresh_ticks = getattr(self, "_auto_log_refresh_ticks", 0) + 1

            nb = getattr(self, "notebook", None)
            if nb is not None:
                try:
                    tab_id = nb.select()
                    tab_text = nb.tab(tab_id, "text")
                except Exception:
                    tab_text = ""
            else:
                tab_text = ""

            refresh_lists = (self._auto_log_refresh_ticks % 6 == 0)  # ~every 60 seconds

            # Refresh both manager and agent viewers so whichever tab the user switches to
            # has up-to-date content. Only the active tab will be visible, but this avoids
            # "stale" logs when toggling between tabs.

            # Manager logs
            if refresh_lists:
                self._refresh_manager_log_files_and_view(select_latest=False)
            else:
                self._refresh_log_view()

            # Agent logs
            if refresh_lists:
                self._refresh_agent_log_files_and_view(select_latest=False)
            else:
                self._refresh_agent_log_view()
        except Exception:
            # Never let the UI crash because of auto-refresh.
            pass
        finally:
            try:
                self.after(10_000, self._auto_refresh_logs)
            except Exception:
                pass

    # ------------------------------------------------------------------ Locked entry + dialog

    def _make_locked_entry_with_dialog(
        self,
        parent,
        row: int,
        label: str,
        var,
        tooltip: str,
        is_int: bool = False,
        allow_empty: bool = False,
    ):
        lbl = ttk.Label(parent, text=label + ":", style="Card.TLabel")
        lbl.grid(row=row, column=0, sticky="w", pady=2)

        entry = ttk.Entry(parent, textvariable=var, state="readonly")
        entry.grid(row=row, column=1, sticky="ew", pady=2)

        # Locked by default; "Change" simply unlocks the field inline.
        btn = ttk.Button(parent, text="Change")
        btn.grid(row=row, column=2, sticky="w", padx=(4, 0), pady=2)

        def toggle_inline_edit():
            # Unlock -> normal, Lock -> readonly
            if str(entry.cget("state")) == "readonly":
                entry.configure(state="normal")
                btn.configure(text="Lock")
                entry.focus_set()
                try:
                    entry.icursor("end")
                except Exception:
                    pass
                return

            # Locking: validate integers if requested.
            val = str(var.get()).strip()
            if is_int:
                if allow_empty and not val:
                    entry.configure(state="readonly")
                    btn.configure(text="Change")
                    return
                try:
                    iv = int(val)
                    if not (1 <= iv <= 65535) and label.lower().startswith("listen"):
                        raise ValueError
                    # Normalize to int-like string (keeps things consistent)
                    var.set(str(iv))
                except Exception:
                    messagebox.showerror(
                        PROJECT_NAME,
                        f"{label} must be a number" + (" (or left blank)." if allow_empty else "."),
                        parent=self,
                    )
                    entry.focus_set()
                    return

            entry.configure(state="readonly")
            btn.configure(text="Change")

        btn.configure(command=toggle_inline_edit)

        add_tooltip(lbl, tooltip)
        add_tooltip(entry, tooltip)
        add_tooltip(btn, "Unlock to edit, then click again to lock.")

    # ------------------------------------------------------------------ Env helpers

    def _available_env_names(self) -> list[str]:
        return [
            name
            for (name, _var, _tip) in self.env_definitions
            if name not in self.active_env_names
        ]

    def _rebuild_env_rows(self):
        for child in self.env_rows_frame.winfo_children():
            child.destroy()
        self._env_entries.clear()
        self._env_delete_buttons.clear()
        self._env_edit_buttons.clear()

        enabled = self.var_env_enabled.get()
        row = 0
        for name in self.active_env_names:
            for n, var, tip in self.env_definitions:
                if n == name:
                    lbl = ttk.Label(
                        self.env_rows_frame,
                        text=name + ":",
                        style="Card.TLabel",
                    )
                    lbl.grid(row=row, column=0, sticky="w", pady=1)
                    add_tooltip(lbl, tip)

                    # Locked by default; unlock per-row using the Edit button.
                    state = "disabled" if not enabled else ("normal" if name in self._env_editing else "readonly")
                    ent = ttk.Entry(self.env_rows_frame, textvariable=var, state=state)
                    ent.grid(row=row, column=1, sticky="ew", pady=1)
                    add_tooltip(ent, tip)

                    def _toggle_edit(nm=name, entry=ent):
                        if nm not in self._env_editing:
                            # Enter edit mode (requires admin because we auto-apply on save)
                            if not self._require_admin():
                                return
                            self._env_editing.add(nm)
                            entry.configure(state="normal")
                            entry.focus_set()
                            entry.icursor("end")
                        else:
                            # Save + lock + apply
                            self._env_editing.discard(nm)
                            entry.configure(state="readonly")
                            self._on_apply()
                        self._rebuild_env_rows()

                    btn_edit = ttk.Button(
                        self.env_rows_frame,
                        text="Save" if name in self._env_editing else "Edit",
                        command=_toggle_edit,
                        width=6,
                    )
                    btn_edit.grid(row=row, column=2, sticky="w", padx=(4, 0))
                    add_tooltip(btn_edit, "Unlock to edit; Save will apply settings (admin required).")

                    btn_del = ttk.Button(
                        self.env_rows_frame,
                        text="Remove",
                        command=lambda nm=name: self._on_env_delete(nm),
                        width=8,
                    )
                    btn_del.grid(row=row, column=3, sticky="w", padx=(4, 0))

                    self._env_entries.append(ent)
                    self._env_edit_buttons.append(btn_edit)
                    self._env_delete_buttons.append(btn_del)
                    row += 1
                    break

        self.env_rows_frame.columnconfigure(1, weight=1)
        self._update_env_enabled_state()

    def _refresh_env_add_controls(self):
        names = self._available_env_names()
        enabled = self.var_env_enabled.get()

        if names:
            self.env_combo.configure(
                values=names,
                state="readonly" if enabled else "disabled",
            )
            if self.var_env_choice.get() not in names:
                self.var_env_choice.set(names[0])
            self.btn_env_add.configure(state="normal" if enabled else "disabled")
        else:
            self.env_combo.configure(values=[], state="disabled")
            self.var_env_choice.set("")
            self.btn_env_add.configure(state="disabled")

    def _update_env_enabled_state(self):
        enabled = self.var_env_enabled.get()

        # Respect per-row locking: when enabled, rows are readonly unless the user
        # explicitly clicked Edit for that row.
        for name, ent in zip(self.active_env_names, self._env_entries):
            if not enabled:
                ent.configure(state="disabled")
            else:
                ent.configure(state="normal" if name in self._env_editing else "readonly")
        for btn in self._env_delete_buttons:
            btn.configure(state="normal" if enabled else "disabled")

        self._refresh_env_add_controls()

    def _on_env_toggle(self):
        self._update_env_enabled_state()

    def _on_env_add(self):
        names = self._available_env_names()
        choice = self.var_env_choice.get()

        if not choice and names:
            choice = names[0]

        if choice and choice not in self.active_env_names:
            self.active_env_names.append(choice)
            self._rebuild_env_rows()

    def _on_env_delete(self, name: str):
        for n, var, _tip in self.env_definitions:
            if n == name:
                var.set("")
                break
        if name in self.active_env_names:
            self.active_env_names.remove(name)
        self._rebuild_env_rows()

    # ------------------------------------------------------------------ Log viewer

    def _refresh_log_view(self):
        # Manager log viewer (supports selecting archived logs)
        p = None
        try:
            label = getattr(self, "var_manager_log_file", None)
            label = label.get() if label is not None else ""
            if label and hasattr(self, "_manager_log_files_map"):
                p = self._manager_log_files_map.get(label)
            if p is None:
                p = LOG_PATH
            if p.exists():
                content = p.read_text(encoding="utf-8", errors="replace")
            else:
                content = "(Log file does not exist yet.)"
        except Exception as exc:
            content = f"Failed to read log file:\n{exc}"

        # Preserve scroll position unless user is already at the bottom.
        try:
            y0, y1 = self.log_text.yview()
            at_bottom = y1 >= 0.999
        except Exception:
            at_bottom = True

        self.log_text.configure(state="normal")
        self.log_text.delete("1.0", tk.END)
        self.log_text.insert(tk.END, content)
        self.log_text.configure(state="disabled")

        if at_bottom:
            self.log_text.see(tk.END)
        else:
            try:
                self.log_text.yview_moveto(y0)
            except Exception:
                pass

    def _refresh_manager_log_files_and_view(self, select_latest: bool = False):
        """Refresh dropdown list of manager log files and update the view."""
        try:
            files = list_manager_log_files()
        except Exception as exc:
            files = []
            log(f"Failed to list manager log files: {exc}")

        values: list[str] = []
        mapping: dict[str, Path] = {}
        for p in files:
            if p == LOG_PATH:
                label = f"Current ({p.name})"
            else:
                label = p.name
            values.append(label)
            mapping[label] = p

        self._manager_log_files_map = mapping
        self.manager_log_combo["values"] = values

        if not values:
            self.var_manager_log_file.set("")
            self._set_log_text("(No manager logs found yet.)")
            return

        cur = self.var_manager_log_file.get().strip()
        if select_latest or (cur not in mapping):
            self.var_manager_log_file.set(values[0])
        self._refresh_log_view()

    def _set_log_text(self, text: str) -> None:
        self.log_text.configure(state="normal")
        self.log_text.delete("1.0", tk.END)
        self.log_text.insert(tk.END, text)
        self.log_text.configure(state="disabled")
        self.log_text.see(tk.END)

    def _on_rotate_manager_logs(self):
        # Manual snapshot + truncate
        try:
            archive = rotate_manager_logs_if_needed(force=True)
            if archive:
                log(f"Manual manager log rotate -> {archive}")
            self._refresh_manager_log_files_and_view(select_latest=True)
        except Exception as exc:
            log(f"Failed to rotate manager logs: {exc}")
            messagebox.showerror(PROJECT_NAME, f"Failed to rotate manager logs:\n{exc}")

    def _on_export_support_bundle(self):
        try:
            outp = create_support_bundle()
            messagebox.showinfo(PROJECT_NAME, f"Support bundle created:\n{outp}")
        except Exception as exc:
            log(f"Failed to create support bundle: {exc}")
            messagebox.showerror(PROJECT_NAME, f"Failed to create support bundle:\n{exc}")

    # ------------------------------------------------------------------ Agent log viewer

    def _refresh_agent_log_files_and_view(self, select_latest: bool = False):
        """Refresh the dropdown list of agent log files and update the view."""
        try:
            files = list_agent_log_files()
        except Exception as exc:
            files = []
            log(f"Failed to list agent log files: {exc}")

        values: list[str] = []
        mapping: dict[str, Path] = {}
        for p in files:
            if p == AGENT_LOG_CURRENT_PATH:
                label = f"Current ({p.name})"
            else:
                label = p.name
            values.append(label)
            mapping[label] = p

        self._agent_log_files_map = mapping
        self.agent_log_combo["values"] = values

        if not values:
            self.var_agent_log_file.set("")
            self._set_agent_log_text("(No agent logs found yet.)")
            return

        current = self.var_agent_log_file.get()
        if select_latest or current not in mapping:
            preferred = "Current (beszel-agent.log)" if "Current (beszel-agent.log)" in mapping else values[0]
            self.var_agent_log_file.set(preferred)

        self._refresh_agent_log_view()

    def _set_agent_log_text(self, content: str) -> None:
        # Preserve scroll position unless user is already at the bottom.
        try:
            y0, y1 = self.agent_log_text.yview()
            at_bottom = y1 >= 0.999
        except Exception:
            at_bottom = True

        self.agent_log_text.configure(state="normal")
        self.agent_log_text.delete("1.0", tk.END)
        self.agent_log_text.insert(tk.END, content)
        self.agent_log_text.configure(state="disabled")

        if at_bottom:
            self.agent_log_text.see(tk.END)
        else:
            try:
                self.agent_log_text.yview_moveto(y0)
            except Exception:
                pass

    def _refresh_agent_log_view(self):
        sel = self.var_agent_log_file.get().strip()
        p = self._agent_log_files_map.get(sel)
        if not p:
            self._set_agent_log_text("(Select a log file.)")
            return

        try:
            if p.exists():
                content = p.read_text(encoding="utf-8", errors="replace")
            else:
                content = "(Selected log file does not exist.)"
        except Exception as exc:
            content = f"Failed to read agent log file:\n{exc}"

        self._set_agent_log_text(content)

    def _on_rotate_agent_logs(self):
        if not self._require_admin():
            return

        def task():
            rotate_agent_logs_and_rename()
            # Refresh UI after rotation
            self._refresh_agent_log_files_and_view(select_latest=False)

        self._run_task("Rotating agent logs...", task)

    # ------------------------------------------------------------------ Misc helpers

    def _open_url(self, url: str):
        try:
            webbrowser.open(url)
        except Exception as exc:
            messagebox.showerror(PROJECT_NAME, f"Failed to open URL:\n{exc}")

    def _open_path(self, path: str) -> None:
        """Open a file/folder path in Explorer (best-effort)."""
        try:
            if os.name == "nt":
                os.startfile(path)  # type: ignore[attr-defined]
            else:
                webbrowser.open(f"file://{path}")
        except Exception as exc:
            messagebox.showerror(PROJECT_NAME, f"Failed to open path:\n{exc}")

    # ------------------------------------------------------------------ Config builder

    def _build_config(self) -> AgentConfig:
        c = self.config_obj

        # LISTEN is optional. If blank, the agent will use its own default.
        listen_raw = self.var_listen.get().strip()
        listen: int | None = None
        if listen_raw:
            try:
                listen = int(listen_raw)
                if not (1 <= listen <= 65535):
                    raise ValueError
            except Exception:
                messagebox.showerror(
                    PROJECT_NAME,
                    "Listen (port) must be a number between 1 and 65535, or left blank.",
                )
                listen = None

        cfg = AgentConfig(
            key=self.var_key.get().strip(),
            token=self.var_token.get().strip(),
            hub_url=self.var_hub_url.get().strip(),
            listen=listen,
            data_dir=self.var_data_dir.get().strip(),
            docker_host=self.var_docker_host.get().strip(),
            exclude_containers=self.var_exclude_containers.get().strip(),
            exclude_smart=self.var_exclude_smart.get().strip(),
            extra_filesystems=self.var_extra_filesystems.get().strip(),
            filesystem=self.var_filesystem.get().strip(),
            intel_gpu_device=self.var_intel_gpu_device.get().strip(),
            key_file=self.var_key_file.get().strip(),
            token_file=self.var_token_file.get().strip(),
            lhm=self.var_lhm.get().strip(),
            log_level=self.var_log_level.get().strip(),
            mem_calc=self.var_mem_calc.get().strip(),
            network=self.var_network.get().strip(),
            nics=self.var_nics.get().strip(),
            sensors=self.var_sensors.get().strip(),
            primary_sensor=self.var_primary_sensor.get().strip(),
            sys_sensors=self.var_sys_sensors.get().strip(),
            service_patterns=self.var_service_patterns.get().strip(),
            smart_devices=self.var_smart_devices.get().strip(),
            system_name=self.var_system_name.get().strip(),
            skip_gpu=self.var_skip_gpu.get().strip(),
            auto_update_enabled=self.var_auto_update.get(),
            update_interval_days=int(self.var_update_interval.get() or 1),
            last_known_version=c.last_known_version,
            debug_logging=self.var_debug_logging.get(),
            start_hidden=not self.var_start_visible.get(),
            first_run_done=c.first_run_done,
        )

        # Attach extra env dynamically for older configs
        extra_disk_usage_cache = self.var_disk_usage_cache.get().strip()
        extra_skip_systemd = self.var_skip_systemd.get().strip()
        if extra_disk_usage_cache:
            setattr(cfg, "disk_usage_cache", extra_disk_usage_cache)
        if extra_skip_systemd:
            setattr(cfg, "skip_systemd", extra_skip_systemd)

        return cfg

    # ------------------------------------------------------------------ Admin / relaunch helpers

    def _relaunch_as_admin(self) -> bool:
        if os.name != "nt":
            return False
        try:
            import ctypes  # type: ignore[attr-defined]
        except Exception:
            return False

        if getattr(sys, "frozen", False):
            exe_path = Path(sys.executable).resolve()
        else:
            exe_path = Path(sys.argv[0]).resolve()

        try:
            rc = ctypes.windll.shell32.ShellExecuteW(  # type: ignore[attr-defined]
                None,
                "runas",
                str(exe_path),
                None,
                None,
                1,
            )
            if rc <= 32:
                log(f"ShellExecuteW(runas) for GUI failed with code {rc}")
                return False
            return True
        except Exception as exc:
            log(f"GUI relaunch as admin failed: {exc}")
            return False

    def _require_admin(self) -> bool:
        if is_admin():
            return True

        res = messagebox.askyesno(
            PROJECT_NAME,
            "This action changes Windows services and requires administrator rights.\n\n"
            "BeszelAgentManager can restart itself as administrator now.\n\n"
            "Do you want to restart BeszelAgentManager as administrator?",
        )
        if not res:
            return False

        if not self._relaunch_as_admin():
            messagebox.showerror(
                PROJECT_NAME,
                "Could not restart BeszelAgentManager as administrator.\n\n"
                "Please close it and start it again using 'Run as administrator'.",
            )
            return False

        try:
            if LOCK_PATH.exists():
                LOCK_PATH.unlink()
        except Exception as exc:
            log(f"Failed to remove lock file before admin relaunch: {exc}")

        sys.exit(0)

    # ------------------------------------------------------------------ Task runner

    def _run_task(self, description: str, func):
        if self._task_running:
            messagebox.showinfo(
                PROJECT_NAME, "Another operation is already in progress."
            )
            return
        self._task_running = True
        self.label_status.config(text=description)
        self.progress.grid()
        self.progress.start(10)

        def worker():
            error = None
            try:
                func()
            except Exception as exc:
                error = exc
                log(f"Task failed: {exc}\n{traceback.format_exc()}")

            def done():
                self.progress.stop()
                self.progress.grid_remove()
                self._task_running = False
                self._update_status()
                self._refresh_log_view()
                if error:
                    messagebox.showerror(
                        PROJECT_NAME, f"Operation failed:\n{error}"
                    )
                else:
                    messagebox.showinfo(
                        PROJECT_NAME, "Operation completed successfully."
                    )

            self.after(0, done)

        threading.Thread(target=worker, daemon=True).start()

    # ------------------------------------------------------------------ Install / update / apply / uninstall

    def _on_install(self):
        if not self._require_admin():
            return

        cfg = self._build_config()
        set_debug_logging(cfg.debug_logging)
        set_autostart(
            self.var_autostart.get(),
            start_hidden=not self.var_start_visible.get(),
        )

        add_defender = False
        if os.name == "nt":
            msg = (
                "BeszelAgentManager can add a Windows Defender exclusion for its own "
                "installation folder (C:\\Program Files\\BeszelAgentManager).\n\n"
                "This helps prevent the manager and the tools it uses from being "
                "blocked or slowed down during agent install/update.\n\n"
                "Do you want to add this Defender exclusion now?"
            )
            add_defender = messagebox.askyesno(PROJECT_NAME, msg)

        def task():
            if add_defender:
                try:
                    ok, reason = ensure_defender_exclusion_for_manager()
                    if not ok:
                        msg = (
                            "BeszelAgentManager could not configure a Windows Defender "
                            "exclusion automatically.\n\n"
                            "If you use third-party antivirus or Windows Defender is "
                            "disabled, please manually add an exclusion for:\n\n"
                            f"- {Path(PROGRAM_FILES) / PROJECT_NAME}\n"
                            f"- {AGENT_DIR}\n"
                            f"- {DATA_DIR}\n\n"
                            f"Details: {reason}"
                        )
                        self.after(
                            0,
                            lambda: messagebox.showwarning(PROJECT_NAME, msg),
                        )
                except Exception as exc:
                    log(f"Defender exclusion failed: {exc}")
                    msg = (
                        "BeszelAgentManager could not configure a Windows Defender "
                        "exclusion automatically.\n\n"
                        "If you use third-party antivirus or Windows Defender is disabled, "
                        "please manually add an exclusion for:\n\n"
                        f"- {Path(PROGRAM_FILES) / PROJECT_NAME}\n"
                        f"- {AGENT_DIR}\n"
                        f"- {DATA_DIR}"
                    )
                    self.after(
                        0,
                        lambda: messagebox.showwarning(PROJECT_NAME, msg),
                    )

            install_or_update_agent_and_service(cfg)
            cfg.save()
            self.config_obj = cfg
            shortcut_mod.ensure_start_menu_shortcut()

        self._run_task("Installing agent and configuring service...", task)

    def _on_update_agent(self):
        if not self._require_admin():
            return

        current = get_agent_version()
        latest_version, changelog = _fetch_latest_agent_release()

        # Build "What's changed" snippet from GitHub release body
        changelog_snippet = ""
        if changelog:
            lines = [ln.rstrip() for ln in changelog.splitlines()]
            while lines and not lines[0]:
                lines.pop(0)
            while lines and not lines[-1]:
                lines.pop()
            max_lines = 12
            if len(lines) > max_lines:
                lines = lines[:max_lines] + [
                    "...",
                    "(truncated; see GitHub release for full notes)",
                ]
            changelog_snippet = "\n\nWhat's changed:\n" + "\n".join(lines)

        # If we know the latest version from GitHub AND the agent reports a version,
        # and they are equal, we just inform the user and do nothing.
        if latest_version and current not in ("Not installed", "Unknown"):
            if current == latest_version:
                info = (
                    f"Agent is already at the latest version {current} (GitHub).\n"
                )
                if changelog_snippet:
                    info += changelog_snippet
                messagebox.showinfo(PROJECT_NAME, info)
                return

        # For all other cases (older version or GitHub lookup failed), fall back to prompt
        msg = None
        if latest_version and current not in ("Not installed", "Unknown"):
            # current != latest_version here
            msg = (
                f"A new Beszel agent version is available.\n\n"
                f"Installed: {current}\n"
                f"Latest on GitHub: {latest_version}."
                f"{changelog_snippet}\n\n"
                "Do you want to update now?"
            )
        elif current not in ("Not installed", "Unknown"):
            # Installed, but we couldn't determine latest from GitHub
            msg = (
                f"Agent is currently at version {current}.\n\n"
                "Could not determine the latest version from GitHub.\n"
                "Do you still want to re-download and restart?"
            )

        if msg is not None:
            if not messagebox.askyesno(PROJECT_NAME, msg):
                return
        # If current is "Not installed"/"Unknown", or GitHub failed, we just run the update task directly.

        def task():
            try:
                stop_service()
            except Exception as exc:
                log(f"Failed to stop service before update: {exc}")
            update_agent_only()
            try:
                start_service()
            except Exception as exc:
                log(f"Failed to start service after update: {exc}")

        self._run_task("Updating agent binary...", task)

    def _on_force_update_agent(self):
        """Force re-download and reinstall of the latest Beszel agent."""
        if not self._require_admin():
            return

        current = get_agent_version()
        latest_version, changelog = _fetch_latest_agent_release()

        # Build "What's changed" snippet from GitHub release body
        changelog_snippet = ""
        if changelog:
            lines = [ln.rstrip() for ln in changelog.splitlines()]
            while lines and not lines[0]:
                lines.pop(0)
            while lines and not lines[-1]:
                lines.pop()
            max_lines = 12
            if len(lines) > max_lines:
                lines = lines[:max_lines] + ["...", "(truncated; see GitHub release for full notes)"]
            changelog_snippet = "\n\nWhat's changed:\n" + "\n".join(lines)

        latest_display = latest_version or "(unknown)"
        msg = (
            "This will re-download and reinstall the Beszel agent, even if you already have the latest version.\n\n"
            f"Installed: {current}\n"
            f"Latest on GitHub: {latest_display}"
            f"{changelog_snippet}\n\n"
            "Do you want to continue?"
        )
        if not messagebox.askyesno(PROJECT_NAME, msg):
            return

        def task():
            try:
                stop_service()
            except Exception as exc:
                log(f"Failed to stop service before force update: {exc}")
            update_agent_only()
            try:
                start_service()
            except Exception as exc:
                log(f"Failed to start service after force update: {exc}")

        self._run_task("Force updating agent binary...", task)

    # ------------------------------------------------------------------ Version pickers

    def _on_agent_versions(self):
        """Open a dialog to install/rollback the Beszel agent to a selected stable version."""
        if not self._require_admin():
            return

        self._open_version_dialog(
            kind="agent",
            title="Manage Agent Version",
            current_version=get_agent_version(),
            fetch_releases=fetch_agent_stable_releases,
            on_install=self._install_selected_agent_release,
        )

    def _on_manage_agent_version(self):
        """Single entry point for managing the agent version (install/rollback/force reinstall)."""
        self._on_agent_versions()

    def _on_manager_versions(self):
        """Open a dialog to install/rollback the manager to a selected stable version."""
        if self._task_running:
            messagebox.showinfo(PROJECT_NAME, "Another operation is already in progress.")
            return

        self._open_version_dialog(
            kind="manager",
            title="Manage Manager Version",
            current_version=APP_VERSION,
            fetch_releases=fetch_stable_releases,
            on_install=self._install_selected_manager_release,
        )

    def _on_manage_manager_version(self):
        """Single entry point for managing the manager version (install/rollback/force reinstall)."""
        self._on_manager_versions()

    def _open_version_dialog(
        self,
        kind: str,
        title: str,
        current_version: str,
        fetch_releases,
        on_install,
    ) -> None:
        """Generic version selector dialog."""

        win = tk.Toplevel(self)
        win.title(title)
        win.geometry("620x420")
        win.transient(self)
        win.grab_set()

        frm = ttk.Frame(win, padding=12)
        frm.pack(fill="both", expand=True)
        frm.columnconfigure(1, weight=1)
        frm.rowconfigure(3, weight=1)

        ttk.Label(frm, text=f"Installed: {current_version}").grid(row=0, column=0, columnspan=2, sticky="w")

        ttk.Label(frm, text="Select version:").grid(row=1, column=0, sticky="w", pady=(10, 4))
        var_sel = tk.StringVar()
        cbo = ttk.Combobox(frm, textvariable=var_sel, state="readonly")
        cbo.grid(row=1, column=1, sticky="ew", pady=(10, 4))

        ttk.Separator(frm, orient="horizontal").grid(row=2, column=0, columnspan=2, sticky="ew", pady=8)

        txt = tk.Text(frm, wrap="word", height=12, state="disabled", font=("Consolas", 9))
        txt.grid(row=3, column=0, columnspan=2, sticky="nsew")

        progress = ttk.Progressbar(frm, mode="indeterminate")
        progress.grid(row=4, column=0, columnspan=2, sticky="ew", pady=(8, 0))
        progress.grid_remove()

        btns = ttk.Frame(frm)
        btns.grid(row=5, column=0, columnspan=2, sticky="e", pady=(10, 0))

        mapping: dict[str, dict] = {}

        def set_text(content: str) -> None:
            txt.configure(state="normal")
            txt.delete("1.0", tk.END)
            txt.insert(tk.END, content)
            txt.configure(state="disabled")

        def on_pick(_evt=None):
            rel = mapping.get(var_sel.get())
            if not rel:
                set_text("")
                return
            body = str(rel.get("body") or "")
            if not body:
                body = "(No release notes.)"
            # Keep dialog responsive: show a short snippet
            lines = [ln.rstrip() for ln in body.splitlines()]
            while lines and not lines[0]:
                lines.pop(0)
            while lines and not lines[-1]:
                lines.pop()
            if len(lines) > 40:
                lines = lines[:40] + ["...", "(truncated)"]
            set_text("\n".join(lines))

        def load_releases():
            progress.grid()
            progress.start(10)

            state = {"releases": [], "error": None}

            def worker():
                try:
                    state["releases"] = fetch_releases() or []
                except Exception as exc:
                    state["error"] = exc

                def done():
                    progress.stop()
                    progress.grid_remove()

                    if state["error"]:
                        messagebox.showerror(PROJECT_NAME, f"Failed to fetch versions:\n{state['error']}")
                        return

                    rels = state["releases"]
                    mapping.clear()
                    values: list[str] = []
                    for r in rels:
                        v = str(r.get("version") or "")
                        tag = str(r.get("tag") or "")
                        if not v:
                            continue
                        label = f"{v} ({tag})" if tag and tag != v else v
                        values.append(label)
                        mapping[label] = r

                    cbo["values"] = values
                    if not values:
                        var_sel.set("")
                        set_text("(No versions found.)")
                        return

                    # Prefer installed version if present in list, else first (newest)
                    pick = None
                    for label, r in mapping.items():
                        if str(r.get("version") or "") == current_version:
                            pick = label
                            break
                    var_sel.set(pick or values[0])
                    on_pick()

                self.after(0, done)

            threading.Thread(target=worker, daemon=True).start()

        def do_install(force: bool):
            rel = mapping.get(var_sel.get())
            if not rel:
                messagebox.showinfo(PROJECT_NAME, "Please select a version first.")
                return

            v = str(rel.get("version") or "")
            if not v:
                messagebox.showerror(PROJECT_NAME, "Selected release has no version.")
                return

            if (not force) and (v == current_version):
                messagebox.showinfo(PROJECT_NAME, f"You are already on {current_version}.")
                return

            def _vt(s: str) -> tuple[int, int, int]:
                m = re.search(r"(\d+)\.(\d+)\.(\d+)", s or "")
                if not m:
                    return (0, 0, 0)
                return (int(m.group(1)), int(m.group(2)), int(m.group(3)))

            warn = "" if _vt(v) >= _vt(current_version) else "\n\nWARNING: This is a downgrade/rollback."
            if not messagebox.askyesno(PROJECT_NAME, f"Install {kind} version {v}?{warn}"):
                return

            on_install(rel, force)

        ttk.Button(btns, text="Refresh", command=load_releases).grid(row=0, column=0, padx=(0, 8))
        ttk.Button(btns, text="Install", command=lambda: do_install(False)).grid(row=0, column=1, padx=(0, 8))
        ttk.Button(btns, text="Force reinstall", command=lambda: do_install(True)).grid(row=0, column=2, padx=(0, 8))
        ttk.Button(btns, text="Close", command=win.destroy).grid(row=0, column=3)

        cbo.bind("<<ComboboxSelected>>", on_pick)
        load_releases()

    def _install_selected_agent_release(self, release: dict, force: bool) -> None:
        """Install a specific agent release (version picker dialog)."""
        version = str(release.get("version") or "").strip()
        changelog = str(release.get("body") or "")

        def task():
            try:
                stop_service()
            except Exception as exc:
                log(f"Failed to stop service before agent version install: {exc}")
            update_agent_only(version=version, changelog=changelog)
            try:
                start_service()
            except Exception as exc:
                log(f"Failed to start service after agent version install: {exc}")

        self._run_task(f"Installing agent {version}...", task)

    def _install_selected_manager_release(self, release: dict, force: bool) -> None:
        """Install a specific manager release (version picker dialog)."""

        # Start update (download + replace) and exit app so the exe can be replaced.
        def worker_update():
            try:
                start_update(release, args=self._current_relaunch_args(), current_pid=os.getpid(), force=force)
            except SystemExit:
                os._exit(0)
            except Exception as exc:
                log(f"Manager version install failed: {exc}\n{traceback.format_exc()}")
                self.after(0, lambda: messagebox.showerror(PROJECT_NAME, f"Manager update failed:\n{exc}"))
                return
            os._exit(0)

        self.label_status.config(text="Updating manager (selected version)...")
        self.progress.grid()
        self.progress.start(10)
        threading.Thread(target=worker_update, daemon=True).start()

    def _on_update_manager(self):
        """Check GitHub for a newer manager release and perform an in-place update."""
        if self._task_running:
            messagebox.showinfo(PROJECT_NAME, "Another operation is already in progress.")
            return

        # ------------------------------------------------------------------ Step 1: check latest release
        self._task_running = True
        self.label_status.config(text="Checking for manager updates...")
        self.progress.grid()
        self.progress.start(10)

        state = {"error": None, "release": None}

        def worker_check():
            try:
                state["release"] = fetch_latest_release()
            except Exception as exc:
                state["error"] = exc
                log(f"Manager update check failed: {exc}\n{traceback.format_exc()}")

            def done_check():
                self.progress.stop()
                self.progress.grid_remove()
                self._task_running = False
                self._update_status()
                self._refresh_log_view()

                if state["error"]:
                    messagebox.showerror(PROJECT_NAME, f"Failed to check for updates:\n{state['error']}")
                    return

                rel = state["release"]
                if not rel:
                    messagebox.showinfo(PROJECT_NAME, "No manager release information found on GitHub.")
                    return

                latest = rel.get("version") or "?"
                tag = rel.get("tag") or "?"
                dl = rel.get("download_url") or "?"
                log(f"Manager update check: current={APP_VERSION} latest={latest} tag={tag} url={dl}")
                if not is_update_available(APP_VERSION, latest):
                    log("Manager update check result: already up-to-date")
                    messagebox.showinfo(PROJECT_NAME, f"You are already on the latest version ({APP_VERSION}).")
                    return

                log("Manager update check result: update available")

                # Build short release notes snippet
                body = str(rel.get("body") or "")
                snippet = ""
                if body:
                    lines = [ln.rstrip() for ln in body.splitlines()]
                    while lines and not lines[0]:
                        lines.pop(0)
                    while lines and not lines[-1]:
                        lines.pop()
                    max_lines = 12
                    if len(lines) > max_lines:
                        lines = lines[:max_lines] + ["...", "(truncated)"]
                    snippet = "\n\nWhat's changed:\n" + "\n".join(lines)

                msg = (
                    f"A new BeszelAgentManager version is available.\n\n"
                    f"Installed: {APP_VERSION}\n"
                    f"Latest on GitHub: {latest}"
                    f"{snippet}\n\n"
                    "Do you want to update now?"
                )
                if not messagebox.askyesno(PROJECT_NAME, msg):
                    return

                # ------------------------------------------------------------------ Step 2: start update (download + replace)
                def worker_update():
                    try:
                        start_update(rel, args=self._current_relaunch_args(), current_pid=os.getpid(), force=False)
                    except SystemExit:
                        # start_update calls sys.exit(0); in a thread this only exits the thread.
                        os._exit(0)
                    except Exception as exc:
                        log(f"Manager update failed: {exc}\n{traceback.format_exc()}")

                        def done_fail():
                            self.progress.stop()
                            self.progress.grid_remove()
                            self._task_running = False
                            self._update_status()
                            self._refresh_log_view()
                            messagebox.showerror(PROJECT_NAME, f"Manager update failed:\n{exc}")

                        self.after(0, done_fail)
                        return

                    # If it ever returns, force exit to allow replacement.
                    os._exit(0)

                self._task_running = True
                self.label_status.config(text="Updating manager (downloading + replacing)...")
                self.progress.grid()
                self.progress.start(10)
                threading.Thread(target=worker_update, daemon=True).start()

            self.after(0, done_check)

        threading.Thread(target=worker_check, daemon=True).start()

    def _on_force_update_manager(self):
        """Force re-download and reinstall of the latest manager release."""
        if self._task_running:
            messagebox.showinfo(PROJECT_NAME, "Another operation is already in progress.")
            return

        self._task_running = True
        self.label_status.config(text="Checking latest manager release...")
        self.progress.grid()
        self.progress.start(10)

        state = {"error": None, "release": None}

        def worker_check():
            try:
                state["release"] = fetch_latest_release()
            except Exception as exc:
                state["error"] = exc
                log(f"Manager force update check failed: {exc}\n{traceback.format_exc()}")

            def done_check():
                self.progress.stop()
                self.progress.grid_remove()
                self._task_running = False
                self._update_status()
                self._refresh_log_view()

                if state["error"]:
                    messagebox.showerror(PROJECT_NAME, f"Failed to check for updates:\n{state['error']}")
                    return

                rel = state["release"]
                if not rel:
                    messagebox.showinfo(PROJECT_NAME, "No manager release information found on GitHub.")
                    return

                latest = rel.get("version") or "?"
                tag = rel.get("tag") or "?"
                dl = rel.get("download_url") or "?"
                log(f"Manager FORCE update: current={APP_VERSION} latest={latest} tag={tag} url={dl}")

                # Build short release notes snippet
                body = str(rel.get("body") or "")
                snippet = ""
                if body:
                    lines = [ln.rstrip() for ln in body.splitlines()]
                    while lines and not lines[0]:
                        lines.pop(0)
                    while lines and not lines[-1]:
                        lines.pop()
                    max_lines = 12
                    if len(lines) > max_lines:
                        lines = lines[:max_lines] + ["...", "(truncated)"]
                    snippet = "\n\nWhat's changed:\n" + "\n".join(lines)

                msg = (
                    "This will redownload and reinstall the latest BeszelAgentManager release.\n\n"
                    f"Installed: {APP_VERSION}\n"
                    f"Latest on GitHub: {latest}\n"
                    f"Tag: {tag}"
                    f"{snippet}\n\n"
                    "Continue?"
                )
                if not messagebox.askyesno(PROJECT_NAME, msg):
                    return

                def worker_update():
                    try:
                        start_update(rel, args=self._current_relaunch_args(), current_pid=os.getpid(), force=True)
                    except SystemExit:
                        os._exit(0)
                    except Exception as exc:
                        log(f"Manager force update failed: {exc}\n{traceback.format_exc()}")

                        def done_fail():
                            self.progress.stop()
                            self.progress.grid_remove()
                            self._task_running = False
                            self._update_status()
                            self._refresh_log_view()
                            messagebox.showerror(PROJECT_NAME, f"Manager force update failed:\n{exc}")

                        self.after(0, done_fail)
                        return

                    os._exit(0)

                self._task_running = True
                self.label_status.config(text="Forcing manager update (downloading + replacing)...")
                self.progress.grid()
                self.progress.start(10)
                threading.Thread(target=worker_update, daemon=True).start()

            self.after(0, done_check)

        threading.Thread(target=worker_check, daemon=True).start()

    def _on_apply(self):
        if not self._require_admin():
            return

        cfg = self._build_config()
        set_debug_logging(cfg.debug_logging)
        set_autostart(
            self.var_autostart.get(), start_hidden=not self.var_start_visible.get()
        )

        def task():
            apply_configuration_only(cfg)
            cfg.save()
            self.config_obj = cfg
            shortcut_mod.ensure_start_menu_shortcut()

        self._run_task("Applying configuration to service...", task)

    def _update_listen_toggle_button(self) -> None:
        """Update the Enable/Disable button based on whether LISTEN is set."""
        try:
            raw = self.var_listen.get().strip()
        except Exception:
            raw = ""
        if getattr(self, "btn_listen_toggle", None) is None:
            return
        self.btn_listen_toggle.configure(text=("Disable" if raw else "Enable"))

    def _on_toggle_listen_firewall(self):
        """Enable or disable LISTEN + Firewall rule depending on current state."""
        raw = self.var_listen.get().strip()

        # ------------------------------------------------------------------ Disable
        if raw:
            if not self._require_admin():
                return

            try:
                port = int(raw)
            except Exception:
                port = DEFAULT_LISTEN_PORT

            if not messagebox.askyesno(
                PROJECT_NAME,
                f"This will clear LISTEN, apply settings, and remove the Windows Firewall rule (port {port}).\n\nContinue?",
            ):
                return

            # Clear LISTEN and apply configuration
            self.var_listen.set("")
            cfg = self._build_config()
            set_debug_logging(cfg.debug_logging)
            set_autostart(
                self.var_autostart.get(), start_hidden=not self.var_start_visible.get()
            )

            def task_disable():
                apply_configuration_only(cfg)
                # Rule name is fixed; removing it is safest.
                remove_firewall_rule()
                cfg.save()
                self.config_obj = cfg

            self._run_task("Disabling listen port + firewall rule...", task_disable)
            return

        # ------------------------------------------------------------------ Enable
        if not self._require_admin():
            return

        # If LISTEN is empty, pick the agent default.
        if not raw:
            self.var_listen.set(str(DEFAULT_LISTEN_PORT))

        cfg = self._build_config()
        if cfg.listen is None:
            cfg.listen = DEFAULT_LISTEN_PORT

        set_debug_logging(cfg.debug_logging)
        set_autostart(
            self.var_autostart.get(), start_hidden=not self.var_start_visible.get()
        )

        def task_enable():
            apply_configuration_only(cfg)
            ensure_firewall_rule(int(cfg.listen))
            cfg.save()
            self.config_obj = cfg

        self._run_task("Enabling listen port + firewall rule...", task_enable)

    # Backward-compat (older code paths may still call this)
    def _on_enable_listen_firewall(self):
        self._on_toggle_listen_firewall()

    def _on_uninstall(self):
        if self._task_running:
            messagebox.showinfo(
                PROJECT_NAME, "Another operation is already in progress."
            )
            return
        if not self._require_admin():
            return

        if not messagebox.askyesno(
            PROJECT_NAME,
            "This will stop and remove the service, firewall rule, scheduled task and agent files.\n\n"
            "Do you want to continue?",
        ):
            return

        remove_self = messagebox.askyesno(
            PROJECT_NAME,
            "Do you also want to remove the BeszelAgentManager application itself,\n"
            "including autostart, the Program Files folder and its ProgramData folder,\n"
            "and then close it now?",
        )

        self._task_running = True
        self.label_status.config(text="Uninstalling agent and cleaning up...")
        self.progress.grid()
        self.progress.start(10)

        from shutil import rmtree

        def worker():
            error = None
            try:
                set_autostart(
                    False, start_hidden=not self.var_start_visible.get()
                )

                try:
                    stop_service()
                except Exception as exc:
                    log(f"Stop service during uninstall failed (may not exist): {exc}")

                delete_update_task()
                delete_agent_log_rotate_task()
                delete_service()
                remove_firewall_rule()

                try:
                    remove_defender_exclusion_for_manager()
                except Exception as exc:
                    log(f"Failed to remove Defender exclusion: {exc}")

                try:
                    if AGENT_DIR.exists():
                        try:
                            run(['takeown', '/f', str(AGENT_DIR), '/r', '/d', 'y'], check=False)
                            run(['icacls', str(AGENT_DIR), '/grant', '*S-1-5-32-544:(OI)(CI)F', '/T', '/C'], check=False)
                            run(['icacls', str(AGENT_DIR), '/grant', '*S-1-5-18:(OI)(CI)F', '/T', '/C'], check=False)
                        except Exception:
                            pass
                        rmtree(AGENT_DIR)
                except Exception as exc:
                    log(f"Failed to remove agent dir {AGENT_DIR}: {exc}")

                # Only attempt to remove ProgramData when we are NOT removing the manager.
                # If we remove the manager, ProgramData cleanup is handled by the delayed
                # self-delete script after this process exits (manager.log is open right now).
                if not remove_self:
                    try:
                        if DATA_DIR.exists():
                            try:
                                run(['takeown', '/f', str(DATA_DIR), '/r', '/d', 'y'], check=False)
                                run(['icacls', str(DATA_DIR), '/grant', '*S-1-5-32-544:(OI)(CI)F', '/T', '/C'], check=False)
                                run(['icacls', str(DATA_DIR), '/grant', '*S-1-5-18:(OI)(CI)F', '/T', '/C'], check=False)
                            except Exception:
                                pass
                            rmtree(DATA_DIR)
                    except Exception as exc:
                        log(f"Failed to remove data dir {DATA_DIR}: {exc}")

                try:
                    if LEGACY_AGENT_DIR.exists():
                        try:
                            run(['takeown', '/f', str(LEGACY_AGENT_DIR), '/r', '/d', 'y'], check=False)
                            run(['icacls', str(LEGACY_AGENT_DIR), '/grant', '*S-1-5-32-544:(OI)(CI)F', '/T', '/C'], check=False)
                            run(['icacls', str(LEGACY_AGENT_DIR), '/grant', '*S-1-5-18:(OI)(CI)F', '/T', '/C'], check=False)
                        except Exception:
                            pass
                        rmtree(LEGACY_AGENT_DIR)
                except Exception as exc:
                    log(f"Failed to remove legacy agent dir {LEGACY_AGENT_DIR}: {exc}")

                shortcut_mod.remove_start_menu_shortcut()
                try:
                    if LOCK_PATH.exists():
                        LOCK_PATH.unlink()
                except Exception as exc:
                    log(f"Failed to remove lock file on uninstall: {exc}")
            except Exception as exc:
                error = exc
                log(f"Uninstall task failed: {exc}\n{traceback.format_exc()}")

            def done():
                self.progress.stop()
                self.progress.grid_remove()
                self._task_running = False
                self._update_status()
                self._refresh_log_view()
                if error:
                    messagebox.showerror(
                        PROJECT_NAME, f"Uninstall failed:\n{error}"
                    )
                else:
                    if remove_self:
                        self._schedule_self_delete_and_exit()
                    else:
                        messagebox.showinfo(
                            PROJECT_NAME, "Uninstall completed successfully."
                        )

            self.after(0, done)

        threading.Thread(target=worker, daemon=True).start()

    # ------------------------------------------------------------------ Service control

    def _on_start_service(self):
        if not self._require_admin():
            return

        def worker():
            try:
                start_service()
            finally:
                self.after(0, self._update_status)
                self.after(0, self._refresh_log_view)

        threading.Thread(target=worker, daemon=True).start()

    def _on_stop_service(self):
        if not self._require_admin():
            return

        def worker():
            forced = False
            try:
                forced = bool(stop_service())
            finally:
                self.after(0, self._update_status)
                self.after(0, self._refresh_log_view)
                if forced:
                    self.after(0, lambda: self._notify_service_forced_kill("stop"))

        threading.Thread(target=worker, daemon=True).start()

    def _on_restart_service(self):
        if not self._require_admin():
            return

        def worker():
            forced = False
            try:
                forced = bool(restart_service())
            finally:
                self.after(0, self._update_status)
                self.after(0, self._refresh_log_view)
                if forced:
                    self.after(0, lambda: self._notify_service_forced_kill("restart"))

        threading.Thread(target=worker, daemon=True).start()

    def _notify_service_forced_kill(self, action: str) -> None:
        """Inform the user that a force-kill was needed and offer diagnostics."""
        try:
            msg = (
                f"The service did not {action} within 30 seconds and was force-killed to recover.\n\n"
                "Do you want to view a diagnostics dump (sc queryex + NSSM settings)?"
            )
            if messagebox.askyesno(PROJECT_NAME, msg):
                diag = get_service_diagnostics()
                self._show_text_window("Service diagnostics", diag)
        except Exception as exc:
            log(f"Failed to show forced-kill diagnostics prompt: {exc}")

    def _show_text_window(self, title: str, text: str) -> None:
        """Show a scrollable read-only text window."""
        win = tk.Toplevel(self)
        win.title(title)
        win.geometry("900x600")

        frame = ttk.Frame(win, padding=10)
        frame.pack(fill="both", expand=True)
        frame.columnconfigure(0, weight=1)
        frame.rowconfigure(0, weight=1)

        txt = tk.Text(frame, wrap="none", font=("Consolas", 9))
        txt.insert("1.0", text or "")
        txt.configure(state="disabled")
        y = ttk.Scrollbar(frame, orient="vertical", command=txt.yview)
        x = ttk.Scrollbar(frame, orient="horizontal", command=txt.xview)
        txt.configure(yscrollcommand=y.set, xscrollcommand=x.set)

        txt.grid(row=0, column=0, sticky="nsew")
        y.grid(row=0, column=1, sticky="ns")
        x.grid(row=1, column=0, sticky="ew")

    # ------------------------------------------------------------------ Status / hub

    def _on_version_clicked(self, _event=None):
        url = (
            "https://github.com/MiranoVerhoef/BeszelAgentManager/"
            f"releases/tag/v{APP_VERSION}"
        )
        self._open_url(url)

    def _open_hub_url(self):
        url = self.var_hub_url.get().strip()
        if not url and self.config_obj:
            url = (self.config_obj.hub_url or "").strip()
        if not url:
            messagebox.showinfo(PROJECT_NAME, "Hub URL is not configured.")
            return
        if not url.startswith("http://") and not url.startswith("https://"):
            url = "https://" + url
        self._open_url(url)

    def _on_hub_clicked(self, _event=None):
        self._open_hub_url()

    def _set_hub_indicator(self, color: str, text: str):
        try:
            self.hub_status_canvas.itemconfigure(
                self._hub_indicator_circle, fill=color, outline=color
            )
        except Exception:
            pass
        self.label_hub_status.config(text=text)

    def _start_hub_ping_loop(self):
        if self._hub_ping_started:
            return
        self._hub_ping_started = True
        self._ping_hub_once()

    def _ping_hub_once(self):
        url = self.var_hub_url.get().strip()
        if not url and self.config_obj:
            url = (self.config_obj.hub_url or "").strip()

        if not url:
            self._set_hub_indicator("#9ca3af", "Hub: Not configured")
            self.after(15000, self._ping_hub_once)
            return

        if not url.startswith("http://") and not url.startswith("https://"):
            url = "https://" + url

        def worker():
            import time
            import urllib.request
            import ssl
            import subprocess

            ping_ms = None
            reachable = False
            try:
                start = time.perf_counter()
                req = urllib.request.Request(url, method="HEAD")
                ctx = ssl.create_default_context()
                with urllib.request.urlopen(req, timeout=5, context=ctx):
                    pass
                ping_ms = int((time.perf_counter() - start) * 1000)
                reachable = True
            except ssl.SSLCertVerificationError as exc:
                # PyInstaller/Python sometimes can't locate the system CA store on Windows.
                # Fall back to PowerShell which uses Windows certificate store.
                try:
                    start = time.perf_counter()
                    safe_url = url.replace("'", "''")
                    ps = (
                        "Invoke-WebRequest -UseBasicParsing -Method Head "
                        f"-Uri '{safe_url}' -TimeoutSec 5 | Out-Null"
                    )
                    creationflags = 0
                    if hasattr(subprocess, "CREATE_NO_WINDOW"):
                        creationflags = subprocess.CREATE_NO_WINDOW
                    cp = subprocess.run(
                        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps],
                        capture_output=True,
                        text=True,
                        timeout=10,
                        creationflags=creationflags,
                    )
                    if cp.returncode == 0:
                        ping_ms = int((time.perf_counter() - start) * 1000)
                        reachable = True
                    else:
                        log(f"Hub ping failed (PowerShell): {(cp.stderr or cp.stdout or '').strip()}")
                        reachable = False
                except Exception as exc2:
                    log(f"Hub ping failed (PowerShell exception): {exc2}")
                    reachable = False
            except Exception as exc:
                log(f"Hub ping failed: {exc}")
                reachable = False

            def done():
                if reachable and ping_ms is not None:
                    color = "#22c55e"
                    text = f"Hub: Reachable ({ping_ms} ms)"
                else:
                    color = "#ef4444"
                    text = "Hub: Unreachable"
                self._set_hub_indicator(color, text)
                try:
                    self.after(15000, self._ping_hub_once)
                except Exception:
                    pass

            try:
                self.after(0, done)
            except Exception:
                pass

        threading.Thread(target=worker, daemon=True).start()

    def _update_status(self):
        status = get_service_status()
        self.label_status.config(text=f"Service status: {status}")
        version = get_agent_version()
        self.label_version.config(text=f"Agent version: {version}")

        hub = check_hub_status(
            self.var_hub_url.get().strip()
            or (self.config_obj.hub_url if self.config_obj else "")
        )
        self._set_hub_indicator("#9ca3af", f"Hub: {hub}")
        self._update_tray_title(status)
        if status in ("START_PENDING", "STOP_PENDING"):
            self.after(1000, self._update_status)

    # ------------------------------------------------------------------ Window / tray / self-delete

    def _on_close(self):
        if self._tray_icon is not None:
            self.withdraw()
        else:
            try:
                if LOCK_PATH.exists():
                    LOCK_PATH.unlink()
            except Exception as exc:
                log(f"Failed to remove lock file on exit: {exc}")
            self.destroy()

    def _schedule_self_delete_and_exit(self):
        try:
            if getattr(sys, "frozen", False):
                exe_path = Path(sys.executable).resolve()
            else:
                exe_path = Path(sys.argv[0]).resolve()

            app_dir = exe_path.parent
            data_dir = DATA_DIR
            agent_dir = AGENT_DIR
            legacy_agent_dir = LEGACY_AGENT_DIR

            if os.name == "nt":
                # Use a dedicated PowerShell cleanup script.
                # This is more reliable than a long cmd /c one-liner and avoids failing
                # to delete ProgramData while manager.log is still open.
                try:
                    import tempfile

                    ps_path = Path(tempfile.gettempdir()) / f"{PROJECT_NAME}_cleanup.ps1"
                    log_path = str(LOG_PATH)

                    def _psq(s: str) -> str:
                        # Single-quote escape for PowerShell
                        return s.replace("'", "''")

                    script = f"""
param(
  [int]$PidToWait,
  [string]$Exe,
  [string]$AppDir,
  [string]$DataDir,
  [string]$AgentDir,
  [string]$LegacyAgentDir,
  [string]$LogPath
)

$ErrorActionPreference = 'SilentlyContinue'

function Log([string]$m) {{
  try {{
    $ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    Add-Content -Path $LogPath -Value "[$ts] $m"
  }} catch {{ }}
}}

function FixAcl([string]$p) {{
  try {{
    if (-not $p -or -not (Test-Path -LiteralPath $p)) {{ return }}
    # Take ownership and grant full control to Builtin Administrators + SYSTEM
    takeown /f "$p" /r /d y | Out-Null
    icacls "$p" /grant *S-1-5-32-544:(OI)(CI)F /T /C | Out-Null
    icacls "$p" /grant *S-1-5-18:(OI)(CI)F /T /C | Out-Null
  }} catch {{ }}
}}

Log "Cleanup: waiting for PID $PidToWait to exit"
try {{ Wait-Process -Id $PidToWait -Timeout 60 }} catch {{ }}

try {{
  # Extra safety: make sure processes are not lingering
  # Also stop/delete the agent service in case a previous uninstall step failed.
  # Use sc.exe explicitly (PowerShell has an alias 'sc' for Set-Content).
  sc.exe stop "{PROJECT_NAME}" | Out-Null
  sc.exe delete "{PROJECT_NAME}" | Out-Null
  taskkill /IM "BeszelAgentManager.exe" /T /F | Out-Null
  taskkill /IM "beszel-agent.exe" /T /F | Out-Null
  taskkill /IM "nssm.exe" /T /F | Out-Null
}} catch {{ }}

$targets = @($Exe, $AppDir, $DataDir, $AgentDir, $LegacyAgentDir)
Log ("Cleanup: targets=" + ($targets -join '; '))

for ($i=0; $i -lt 120; $i++) {{
  foreach ($t in $targets) {{
    if ([string]::IsNullOrWhiteSpace($t)) {{ continue }}
    try {{
      if (Test-Path -LiteralPath $t) {{
        FixAcl $t
        Remove-Item -LiteralPath $t -Recurse -Force -ErrorAction SilentlyContinue
        # If a directory still remains, try cmd rmdir as a fallback
        if (Test-Path -LiteralPath $t) {{
          cmd /c rmdir /s /q "\"$t\"" | Out-Null
        }}
      }}
    }} catch {{ }}
  }}

  $remaining = @($targets | Where-Object {{ $_ -and (Test-Path -LiteralPath $_) }})
  if ($remaining.Count -eq 0) {{
    Log "Cleanup: all targets removed"
    exit 0
  }}
  Start-Sleep -Milliseconds 500
}}

Log "Cleanup: timed out; remaining="
try {{ $remaining | ForEach-Object {{ Log $_ }} }} catch {{ }}
"""
                    ps_path.write_text(script, encoding="utf-8")

                    args = [
                        "powershell.exe",
                        "-NoProfile",
                        "-ExecutionPolicy",
                        "Bypass",
                        "-File",
                        str(ps_path),
                        "-PidToWait",
                        str(os.getpid()),
                        "-Exe",
                        str(exe_path),
                        "-AppDir",
                        str(app_dir),
                        "-DataDir",
                        str(data_dir),
                        "-AgentDir",
                        str(agent_dir),
                        "-LegacyAgentDir",
                        str(legacy_agent_dir),
                        "-LogPath",
                        log_path,
                    ]

                    creationflags = 0
                    if hasattr(subprocess, "CREATE_NO_WINDOW"):
                        creationflags = subprocess.CREATE_NO_WINDOW

                    subprocess.Popen(args, creationflags=creationflags)
                    log(
                        f"Scheduled cleanup via PowerShell: exe={exe_path}, app_dir={app_dir}, data_dir={data_dir}, "
                        f"agent_dir={agent_dir}, legacy_agent_dir={legacy_agent_dir}"
                    )
                except Exception as exc:
                    log(f"Failed to schedule cleanup: {exc}")
        finally:
            try:
                if LOCK_PATH.exists():
                    LOCK_PATH.unlink()
            except Exception as exc:
                log(f"Failed to remove lock file on self-delete: {exc}")
            try:
                self.destroy()
            finally:
                # Ensure the process exits so the cleanup script can remove files.
                os._exit(0)

    def _create_tray_image(self):
        if pystray is None:
            return None
        try:
            icon_path = _resource_path("BeszelAgentManager_icon_512.png")
            img = Image.open(icon_path)
            return img
        except Exception:
            size = 16
            img = Image.new("RGB", (size, size))
            dc = ImageDraw.Draw(img)
            dc.rectangle((1, 1, size - 2, size - 2))
            return img

    def _tray_open(self, _icon, _item):
        self.after(0, lambda: [self.deiconify(), self.lift(), self.focus_force()])

    def _tray_open_hub(self, _icon, _item):
        self.after(0, self._open_hub_url)

    def _tray_update_now(self, _icon, _item):
        self.after(0, self._on_update_agent)

    def _tray_exit(self, icon, _item):
        self.after(0, self._exit_from_tray)
        icon.stop()

    def _tray_start_service(self, _icon, _item):
        self.after(0, self._on_start_service)

    def _tray_stop_service(self, _icon, _item):
        self.after(0, self._on_stop_service)

    def _tray_restart_service(self, _icon, _item):
        self.after(0, self._on_restart_service)

    def _init_tray(self):
        if pystray is None:
            return
        img = self._create_tray_image()
        if img is None:
            return
        menu = pystray.Menu(
            pystray.MenuItem(
                "Open BeszelAgentManager", self._tray_open, default=True
            ),
            pystray.MenuItem("Open hub URL", self._tray_open_hub),
            pystray.MenuItem("Start service", self._tray_start_service),
            pystray.MenuItem("Stop service", self._tray_stop_service),
            pystray.MenuItem("Restart service", self._tray_restart_service),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Update agent now", self._tray_update_now),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Exit", self._tray_exit),
        )
        icon = pystray.Icon(PROJECT_NAME, img, PROJECT_NAME, menu)

        def run_icon():
            icon.run()

        t = threading.Thread(target=run_icon, daemon=True)
        t.start()
        self._tray_icon = icon

    def _update_tray_title(self, status: str):
        if self._tray_icon is None:
            return
        try:
            self._tray_icon.title = f"{PROJECT_NAME} ({status})"
        except Exception:
            pass

    def _exit_from_tray(self):
        try:
            if LOCK_PATH.exists():
                LOCK_PATH.unlink()
        except Exception as exc:
            log(f"Failed to remove lock file on tray exit: {exc}")
        self.destroy()


def main(start_hidden: bool = False):
    app = BeszelAgentManagerApp(start_hidden=start_hidden)
    app.mainloop()
