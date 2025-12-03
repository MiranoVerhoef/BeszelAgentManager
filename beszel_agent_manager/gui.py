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
)
from .agent_manager import (
    install_or_update_agent_and_service,
    apply_configuration_only,
    get_agent_version,
    update_agent_only,
    _parse_download_version,
    check_hub_status,
)
from .windows_service import (
    get_service_status,
    delete_service,
    remove_firewall_rule,
    start_service,
    stop_service,
    restart_service,
)
from .scheduler import delete_update_task
from .util import log, set_debug_logging
from .autostart import (
    get_autostart_state,
    set_autostart,
    is_autostart_enabled,  # kept for compatibility even if unused
)
from . import shortcut as shortcut_mod
from .defender import (
    ensure_defender_exclusion_for_manager,
    remove_defender_exclusion_for_manager,
)
from .bootstrap import is_admin


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


# ---------------------------------------------------------------------------
# Main application
# ---------------------------------------------------------------------------

class BeszelAgentManagerApp(tk.Tk):
    def __init__(self, start_hidden: bool = False) -> None:
        super().__init__()

        # Start fully hidden, decide visibility later
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

        # Apply current autostart state at runtime
        set_autostart(
            self.var_autostart.get(),
            start_hidden=not self.var_start_visible.get(),
        )

        # Decide whether to show the main window.
        # We started withdrawn; only deiconify when we actually want it visible.
        hide = (
            start_hidden
            and self.var_autostart.get()
            and not self._first_run
            and not self.var_start_visible.get()
        )

        if not hide:
            # Normal run OR first run OR "show window on startup" enabled
            self.deiconify()

        self._start_hub_ping_loop()

    # ------------------------------------------------------------------ Vars / autosave

    def _build_vars(self):
        c = self.config_obj

        # Core connection settings
        self.var_key = tk.StringVar(value=c.key)
        self.var_token = tk.StringVar(value=c.token)
        self.var_hub_url = tk.StringVar(value=c.hub_url)
        self.var_listen = tk.IntVar(value=c.listen or DEFAULT_LISTEN_PORT)

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

        # Auto update
        self.var_auto_update = tk.BooleanVar(value=c.auto_update_enabled)
        self.var_update_interval = tk.IntVar(value=c.update_interval_days or 1)

        # Logging & startup
        self.var_debug_logging = tk.BooleanVar(value=c.debug_logging)

        # Read back the actual Run-key so checkboxes reflect reality
        enabled, start_hidden_flag = get_autostart_state()
        self.var_autostart = tk.BooleanVar(value=enabled)

        # "Show window when starting with Windows" is the inverse of "start hidden"
        self.var_start_visible = tk.BooleanVar(
            value=enabled and not start_hidden_flag
        )

        # Environment variables configuration
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
        ]

        self.active_env_names: list[str] = [
            name for (name, var, _tip) in self.env_definitions if var.get().strip()
        ]

        self.var_env_enabled = tk.BooleanVar(value=self._any_env_nonempty())

        self._env_entries: list[ttk.Entry] = []
        self._env_delete_buttons: list[ttk.Button] = []

        # Autosave wiring
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
            self.var_auto_update,
            self.var_update_interval,
            self.var_debug_logging,
            self.var_autostart,
            self.var_start_visible,
        )
        for v in autosave_vars:
            v.trace_add("write", self._on_var_changed)

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
        notebook.grid(row=0, column=0, sticky="nsew")

        # ------------------------------------------------------------------ Connection tab
        conn = ttk.Frame(notebook, padding=10, style="Card.TFrame")
        conn.columnconfigure(1, weight=1)
        conn.columnconfigure(2, weight=0)
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
            tooltip="Agent listen port (LISTEN), default 45876.",
            is_int=True,
        )

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
        log_tab.rowconfigure(3, weight=1)
        notebook.add(log_tab, text="Logging")

        chk_debug = ttk.Checkbutton(
            log_tab, text="Enable debug logging", variable=self.var_debug_logging
        )
        chk_debug.grid(row=0, column=0, sticky="w")
        add_tooltip(
            chk_debug,
            "When enabled, detailed operations are written to manager.log.",
        )

        ttk.Separator(log_tab, orient="horizontal").grid(
            row=1, column=0, sticky="ew", pady=8
        )

        lbl_path = ttk.Label(
            log_tab,
            text=f"Log file: {LOG_PATH}",
            style="Card.TLabel",
        )
        lbl_path.grid(row=2, column=0, sticky="w")

        log_frame = ttk.Frame(log_tab, style="Card.TFrame", padding=(4, 4, 4, 4))
        log_frame.grid(row=3, column=0, sticky="nsew", pady=(8, 0))
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

        btn_refresh_log = ttk.Button(
            log_tab, text="Refresh log", command=self._refresh_log_view
        )
        btn_refresh_log.grid(row=4, column=0, sticky="w", pady=(6, 0))

        self._refresh_log_view()

        # ------------------------------------------------------------------ Bottom section
        outer.rowconfigure(2, weight=0)
        outer.rowconfigure(3, weight=0)

        bottom = ttk.Frame(outer, style="App.TFrame")
        bottom.grid(row=2, column=0, sticky="ew", pady=(10, 4))
        for i in range(6):
            bottom.columnconfigure(i, weight=1 if i == 0 else 0)

        status_frame = ttk.Frame(outer, style="App.TFrame")
        status_frame.grid(row=3, column=0, sticky="ew")
        status_frame.columnconfigure(0, weight=1)

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

        # Bottom buttons + hyperlinks
        link_about = ttk.Label(bottom, text="About", style="Link.TLabel")
        link_about.grid(row=0, column=0, padx=4, pady=4, sticky="w")
        link_about.bind(
            "<Button-1>",
            lambda e: self._open_url(
                "https://github.com/MiranoVerhoef/BeszelAgentManager"
            ),
        )
        link_about.bind("<Enter>", lambda e: link_about.configure(cursor="hand2"))
        link_about.bind("<Leave>", lambda e: link_about.configure(cursor=""))

        link_about_beszel = ttk.Label(bottom, text="About Beszel", style="Link.TLabel")
        link_about_beszel.grid(row=0, column=1, padx=4, pady=4, sticky="w")
        link_about_beszel.bind(
            "<Button-1>", lambda e: self._open_url("https://beszel.dev")
        )
        link_about_beszel.bind(
            "<Enter>", lambda e: link_about_beszel.configure(cursor="hand2")
        )
        link_about_beszel.bind(
            "<Leave>", lambda e: link_about_beszel.configure(cursor="")
        )

        btn_install = ttk.Button(
            bottom,
            text="Install agent",
            command=self._on_install,
        )
        btn_install.grid(row=0, column=2, padx=4, pady=4, sticky="e")

        btn_update = ttk.Button(
            bottom,
            text="Update agent",
            command=self._on_update_agent,
        )
        btn_update.grid(row=0, column=3, padx=4, pady=4, sticky="e")

        btn_apply = ttk.Button(
            bottom,
            text="Apply settings",
            command=self._on_apply,
        )
        btn_apply.grid(row=0, column=4, padx=4, pady=4, sticky="e")

        btn_uninstall = ttk.Button(
            bottom,
            text="Uninstall agent",
            command=self._on_uninstall,
        )
        btn_uninstall.grid(row=0, column=5, padx=4, pady=4, sticky="e")

        self.protocol("WM_DELETE_WINDOW", self._on_close)

    # ------------------------------------------------------------------ Locked entry + dialog

    def _make_locked_entry_with_dialog(
        self,
        parent,
        row: int,
        label: str,
        var,
        tooltip: str,
        is_int: bool = False,
    ):
        """
        Show the value in a read-only entry, but edit via a small dialog when
        clicking the "Change" button. This avoids accidental edits.
        """
        lbl = ttk.Label(parent, text=label + ":", style="Card.TLabel")
        lbl.grid(row=row, column=0, sticky="w", pady=2)

        entry = ttk.Entry(parent, textvariable=var, state="readonly")
        entry.grid(row=row, column=1, sticky="ew", pady=2)

        btn = ttk.Button(parent, text="Change")
        btn.grid(row=row, column=2, sticky="w", padx=(4, 0), pady=2)

        def open_dialog():
            top = tk.Toplevel(self)
            top.title(f"Change {label}")
            top.transient(self)
            top.grab_set()
            top.resizable(False, False)

            frm = ttk.Frame(top, padding=10)
            frm.grid(row=0, column=0, sticky="nsew")
            frm.columnconfigure(1, weight=1)

            ttk.Label(frm, text=f"{label}:", style="Card.TLabel").grid(
                row=0, column=0, sticky="w"
            )

            current_value = str(var.get())
            tmp_var = tk.StringVar(value=current_value)

            ent = ttk.Entry(frm, textvariable=tmp_var)
            ent.grid(row=0, column=1, sticky="ew", padx=(6, 0))
            ent.focus_set()
            ent.icursor("end")

            btn_frame = ttk.Frame(frm)
            btn_frame.grid(row=1, column=0, columnspan=2, sticky="e", pady=(10, 0))

            def on_save():
                val = tmp_var.get()
                if is_int:
                    try:
                        iv = int(val)
                    except ValueError:
                        messagebox.showerror(
                            PROJECT_NAME,
                            f"{label} must be a number.",
                            parent=top,
                        )
                        return
                    var.set(iv)
                else:
                    var.set(val)
                top.destroy()

            def on_cancel():
                top.destroy()

            btn_ok = ttk.Button(btn_frame, text="Save", command=on_save)
            btn_ok.grid(row=0, column=0, padx=(0, 6))

            btn_cancel = ttk.Button(btn_frame, text="Cancel", command=on_cancel)
            btn_cancel.grid(row=0, column=1)

            self.update_idletasks()
            x = self.winfo_rootx() + (self.winfo_width() // 2) - (top.winfo_reqwidth() // 2)
            y = self.winfo_rooty() + (self.winfo_height() // 2) - (top.winfo_reqheight() // 2)
            top.geometry(f"+{x}+{y}")

        btn.configure(command=open_dialog)

        add_tooltip(lbl, tooltip)
        add_tooltip(entry, tooltip)
        add_tooltip(btn, "Open a dialog to change this value safely.")

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

                    ent = ttk.Entry(self.env_rows_frame, textvariable=var)
                    ent.grid(row=row, column=1, sticky="ew", pady=1)
                    add_tooltip(ent, tip)

                    btn_del = ttk.Button(
                        self.env_rows_frame,
                        text="Remove",
                        command=lambda nm=name: self._on_env_delete(nm),
                        width=8,
                    )
                    btn_del.grid(row=row, column=2, sticky="w", padx=(4, 0))

                    self._env_entries.append(ent)
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
        entry_state = "normal" if enabled else "disabled"

        for ent in self._env_entries:
            ent.configure(state=entry_state)
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
        try:
            if LOG_PATH.exists():
                content = LOG_PATH.read_text(encoding="utf-8", errors="replace")
            else:
                content = "(Log file does not exist yet.)"
        except Exception as exc:
            content = f"Failed to read log file:\n{exc}"

        self.log_text.configure(state="normal")
        self.log_text.delete("1.0", tk.END)
        self.log_text.insert(tk.END, content)
        self.log_text.configure(state="disabled")
        self.log_text.see(tk.END)

    # ------------------------------------------------------------------ Misc helpers

    def _open_url(self, url: str):
        try:
            webbrowser.open(url)
        except Exception as exc:
            messagebox.showerror(PROJECT_NAME, f"Failed to open URL:\n{exc}")

    # ------------------------------------------------------------------ Config builder

    def _build_config(self) -> AgentConfig:
        c = self.config_obj
        cfg = AgentConfig(
            key=self.var_key.get().strip(),
            token=self.var_token.get().strip(),
            hub_url=self.var_hub_url.get().strip(),
            listen=int(self.var_listen.get() or DEFAULT_LISTEN_PORT),
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
            self.var_autostart.get(), start_hidden=not self.var_start_visible.get()
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
        target = _parse_download_version()
        if current not in ("Not installed", "Unknown") and target and current == target:
            if not messagebox.askyesno(
                PROJECT_NAME,
                f"Agent is already at version {current}.\nForce re-download and restart?",
            ):
                return

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
                delete_service()
                remove_firewall_rule()

                try:
                    remove_defender_exclusion_for_manager()
                except Exception as exc:
                    log(f"Failed to remove Defender exclusion: {exc}")

                try:
                    if AGENT_DIR.exists():
                        rmtree(AGENT_DIR)
                except Exception as exc:
                    log(f"Failed to remove agent dir {AGENT_DIR}: {exc}")

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
            try:
                stop_service()
            finally:
                self.after(0, self._update_status)
                self.after(0, self._refresh_log_view)

        threading.Thread(target=worker, daemon=True).start()

    def _on_restart_service(self):
        if not self._require_admin():
            return

        def worker():
            try:
                restart_service()
            finally:
                self.after(0, self._update_status)
                self.after(0, self._refresh_log_view)

        threading.Thread(target=worker, daemon=True).start()

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

            ping_ms = None
            reachable = False
            try:
                start = time.perf_counter()
                req = urllib.request.Request(url, method="HEAD")
                with urllib.request.urlopen(req, timeout=5):
                    pass
                ping_ms = int((time.perf_counter() - start) * 1000)
                reachable = True
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

            if os.name == "nt":
                cmd_str = (
                    "timeout /t 2 /nobreak >nul & "
                    f'taskkill /PID {os.getpid()} /F >nul 2>&1 & '
                    f'del \"{exe_path}\" /Q >nul 2>&1 & '
                    f'rmdir /S /Q \"{app_dir}\" >nul 2>&1 & '
                    f'rmdir /S /Q \"{data_dir}\" >nul 2>&1 & '
                    f'rmdir /S /Q \"{agent_dir}\" >nul 2>&1'
                )
                cmd = ["cmd", "/c", cmd_str]
                creationflags = 0
                if hasattr(subprocess, "CREATE_NO_WINDOW"):
                    creationflags = subprocess.CREATE_NO_WINDOW
                try:
                    subprocess.Popen(cmd, creationflags=creationflags)
                    log(
                        f"Scheduled self-delete for {exe_path}, {app_dir}, {data_dir}, {agent_dir}"
                    )
                except Exception as exc:
                    log(f"Failed to schedule self-delete: {exc}")
        finally:
            try:
                if LOCK_PATH.exists():
                    LOCK_PATH.unlink()
            except Exception as exc:
                log(f"Failed to remove lock file on self-delete: {exc}")
            self.destroy()

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
