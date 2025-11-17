from __future__ import annotations
import threading
import traceback
import tkinter as tk
from tkinter import ttk, messagebox
import os
import subprocess
from pathlib import Path
import sys
import webbrowser

try:
    import pystray  # type: ignore
    from PIL import Image, ImageDraw  # type: ignore
except Exception:
    pystray = None  # type: ignore

from .config import AgentConfig
from .constants import PROJECT_NAME, APP_VERSION, DEFAULT_LISTEN_PORT, AGENT_DIR, LOG_PATH, LOCK_PATH
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
from . import autostart
from . import shortcut as shortcut_mod


def _resource_path(relative: str) -> str:
    if hasattr(sys, "_MEIPASS"):
        return str(Path(sys._MEIPASS) / relative)
    return str(Path(__file__).resolve().parent.parent / relative)


class ToolTip:
    def __init__(self, widget, text: str):
        self.widget = widget
        self.text = text
        self.tip = None
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


class BeszelAgentManagerApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title(f"{PROJECT_NAME} v{APP_VERSION}")
        self.geometry("880x640")
        self.minsize(780, 520)

        try:
            self.iconbitmap(_resource_path("BeszelAgentManager_icon.ico"))
        except Exception:
            pass

        style = ttk.Style()
        try:
            style.theme_use("vista")
        except tk.TclError:
            style.theme_use(style.theme_names()[0])

        self.config_obj = AgentConfig.load()
        set_debug_logging(self.config_obj.debug_logging)

        self._task_running = False
        self._tray_icon = None

        self._build_vars()
        self._build_ui()
        self._update_status()
        self._init_tray()

    def _build_vars(self):
        c = self.config_obj
        self.var_key = tk.StringVar(value=c.key)
        self.var_token = tk.StringVar(value=c.token)
        self.var_hub_url = tk.StringVar(value=c.hub_url)
        self.var_listen = tk.IntVar(value=c.listen or DEFAULT_LISTEN_PORT)

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

        self.var_auto_update = tk.BooleanVar(value=c.auto_update_enabled)
        self.var_update_interval = tk.IntVar(value=c.update_interval_days or 1)

        self.var_debug_logging = tk.BooleanVar(value=c.debug_logging)
        self.var_autostart = tk.BooleanVar(value=autostart.is_autostart_enabled())

    def _build_ui(self):
        self.columnconfigure(0, weight=1)
        self.rowconfigure(0, weight=1)
        outer = ttk.Frame(self, padding=12)
        outer.grid(row=0, column=0, sticky="nsew")
        outer.columnconfigure(0, weight=1)
        outer.rowconfigure(0, weight=1)

        notebook = ttk.Notebook(outer)
        notebook.grid(row=0, column=0, sticky="nsew", pady=(0, 8))

        conn = ttk.Frame(notebook, padding=10)
        conn.columnconfigure(1, weight=1)
        notebook.add(conn, text="Connection")

        self._entry_with_paste(conn, 0, "Key", self.var_key, "Public key from Beszel hub (KEY).")
        self._entry_with_paste(conn, 1, "Token", self.var_token, "Optional token (TOKEN).")
        self._entry_with_paste(conn, 2, "Hub URL", self.var_hub_url, "Monitoring / hub URL (HUB_URL).")
        self._entry_with_paste(conn, 3, "Listen (port)", self.var_listen, "Agent listen port (LISTEN), default 45876.")

        auto = ttk.LabelFrame(conn, text="Automatic updates", padding=8)
        auto.grid(row=4, column=0, columnspan=3, sticky="ew", pady=(12, 0))
        auto.columnconfigure(1, weight=1)

        chk = ttk.Checkbutton(auto, text="Enable automatic updates", variable=self.var_auto_update)
        chk.grid(row=0, column=0, columnspan=2, sticky="w")
        add_tooltip(chk, "Use a scheduled task to run 'beszel-agent update' every N days.")

        ttk.Label(auto, text="Interval (days):").grid(row=1, column=0, sticky="w", pady=(6, 0))
        spin = ttk.Spinbox(auto, from_=1, to=90, textvariable=self.var_update_interval, width=6)
        spin.grid(row=1, column=1, sticky="w", pady=(6, 0))

        startup = ttk.LabelFrame(conn, text="Manager startup", padding=8)
        startup.grid(row=5, column=0, columnspan=3, sticky="ew", pady=(12, 0))
        chk_auto = ttk.Checkbutton(
            startup,
            text="Start BeszelAgentManager with Windows",
            variable=self.var_autostart,
        )
        chk_auto.grid(row=0, column=0, sticky="w")
        add_tooltip(
            chk_auto,
            "Create a Run-key entry so the GUI and tray icon start automatically after logon.",
        )

        svc = ttk.LabelFrame(conn, text="Service control", padding=8)
        svc.grid(row=6, column=0, columnspan=3, sticky="ew", pady=(12, 0))
        btn_s_start = ttk.Button(svc, text="Start service", command=self._on_start_service)
        btn_s_start.grid(row=0, column=0, padx=(0, 6), pady=2, sticky="w")
        add_tooltip(btn_s_start, "Start the BeszelAgentManager Windows service.")

        btn_s_stop = ttk.Button(svc, text="Stop service", command=self._on_stop_service)
        btn_s_stop.grid(row=0, column=1, padx=(0, 6), pady=2, sticky="w")
        add_tooltip(btn_s_stop, "Stop the BeszelAgentManager Windows service.")

        btn_s_restart = ttk.Button(svc, text="Restart service", command=self._on_restart_service)
        btn_s_restart.grid(row=0, column=2, padx=(0, 6), pady=2, sticky="w")
        add_tooltip(btn_s_restart, "Restart the BeszelAgentManager Windows service.")

        adv = ttk.Frame(notebook, padding=10)
        adv.columnconfigure(1, weight=1)
        notebook.add(adv, text="Advanced env")

        row = 0
        for label, var, tip in [
            ("DATA_DIR", self.var_data_dir, "Custom data dir (DATA_DIR)."),
            ("DOCKER_HOST", self.var_docker_host, "Docker host override."),
            ("EXCLUDE_CONTAINERS", self.var_exclude_containers, "Containers to exclude."),
            ("EXCLUDE_SMART", self.var_exclude_smart, "Disks to exclude from SMART."),
            ("EXTRA_FILESYSTEMS", self.var_extra_filesystems, "Extra paths/disks."),
            ("FILESYSTEM", self.var_filesystem, "Root filesystem override."),
            ("INTEL_GPU_DEVICE", self.var_intel_gpu_device, "intel_gpu_top device."),
            ("KEY_FILE", self.var_key_file, "File with KEY."),
            ("TOKEN_FILE", self.var_token_file, "File with TOKEN."),
            ("LHM", self.var_lhm, "Enable LibreHardwareMonitor."),
            ("LOG_LEVEL", self.var_log_level, "Log level."),
            ("MEM_CALC", self.var_mem_calc, "Memory calc mode."),
            ("NETWORK", self.var_network, "Network mode."),
            ("NICS", self.var_nics, "Interfaces to include."),
            ("SENSORS", self.var_sensors, "Sensors list."),
            ("PRIMARY_SENSOR", self.var_primary_sensor, "Primary temperature sensor."),
            ("SYS_SENSORS", self.var_sys_sensors, "System sensors path."),
            ("SERVICE_PATTERNS", self.var_service_patterns, "Service patterns."),
            ("SMART_DEVICES", self.var_smart_devices, "SMART devices."),
            ("SYSTEM_NAME", self.var_system_name, "Override system name."),
            ("SKIP_GPU", self.var_skip_gpu, "Skip GPU stats."),
        ]:
            self._adv_entry(adv, row, label, var, tip)
            row += 1

        log_tab = ttk.Frame(notebook, padding=10)
        log_tab.columnconfigure(0, weight=1)
        notebook.add(log_tab, text="Logging")

        chk_debug = ttk.Checkbutton(log_tab, text="Enable debug logging", variable=self.var_debug_logging)
        chk_debug.grid(row=0, column=0, sticky="w")
        add_tooltip(chk_debug, "When enabled, commands and their output are written to manager.log.")

        ttk.Separator(log_tab, orient="horizontal").grid(row=1, column=0, sticky="ew", pady=8)

        lbl_path = ttk.Label(log_tab, text=f"Log file: {LOG_PATH}")
        lbl_path.grid(row=2, column=0, sticky="w")

        btn_open_log = ttk.Button(log_tab, text="Open log folder", command=self._open_log_folder)
        btn_open_log.grid(row=3, column=0, sticky="w", pady=(4, 0))

        outer.rowconfigure(1, weight=0)
        outer.rowconfigure(2, weight=0)

        bottom = ttk.Frame(outer)
        bottom.grid(row=1, column=0, sticky="ew")
        for i in range(6):
            bottom.columnconfigure(i, weight=1 if i == 0 else 0)

        status_frame = ttk.Frame(outer)
        status_frame.grid(row=2, column=0, sticky="ew")
        status_frame.columnconfigure(0, weight=1)

        self.label_app_version = ttk.Label(status_frame, text=f"{PROJECT_NAME} v{APP_VERSION}")
        self.label_app_version.grid(row=0, column=0, sticky="w")

        self.label_status = ttk.Label(status_frame, text="Service status: Unknown")
        self.label_status.grid(row=1, column=0, sticky="w")

        self.label_version = ttk.Label(status_frame, text="Agent version: Not installed")
        self.label_version.grid(row=2, column=0, sticky="w")

        self.label_hub_status = ttk.Label(status_frame, text="Hub: Unknown")
        self.label_hub_status.grid(row=3, column=0, sticky="w")

        self.progress = ttk.Progressbar(status_frame, mode="indeterminate", length=180)
        self.progress.grid(row=0, column=1, rowspan=4, sticky="e")
        self.progress.grid_remove()

        btn_about = ttk.Button(bottom, text="About", command=self._on_about)
        btn_about.grid(row=0, column=0, padx=4, pady=4, sticky="w")
        add_tooltip(btn_about, "About BeszelAgentManager.")

        btn_about_beszel = ttk.Button(bottom, text="About Beszel", command=self._on_about_beszel)
        btn_about_beszel.grid(row=0, column=1, padx=4, pady=4, sticky="w")
        add_tooltip(btn_about_beszel, "About the Beszel monitoring project.")

        btn_install = ttk.Button(bottom, text="Install agent", command=self._on_install)
        btn_install.grid(row=0, column=2, padx=4, pady=4, sticky="e")
        add_tooltip(btn_install, "Download agent, create/update service and firewall, configure auto-update.")

        btn_update = ttk.Button(bottom, text="Update agent", command=self._on_update_agent)
        btn_update.grid(row=0, column=3, padx=4, pady=4, sticky="e")
        add_tooltip(btn_update, "Re-download the agent binary and restart the service.")

        btn_apply = ttk.Button(bottom, text="Apply settings", command=self._on_apply)
        btn_apply.grid(row=0, column=4, padx=4, pady=4, sticky="e")
        add_tooltip(btn_apply, "Apply new configuration without re-downloading the agent.")

        btn_uninstall = ttk.Button(bottom, text="Uninstall agent", command=self._on_uninstall)
        btn_uninstall.grid(row=0, column=5, padx=4, pady=4, sticky="e")
        add_tooltip(btn_uninstall, "Remove service, firewall rule, scheduled task, agent files and shortcut.")

        self.protocol("WM_DELETE_WINDOW", self._on_close)

    def _entry_with_paste(self, parent, row, label, var, tooltip: str):
        lbl = ttk.Label(parent, text=label + ":")
        lbl.grid(row=row, column=0, sticky="w", pady=2)
        ent = ttk.Entry(parent, textvariable=var)
        ent.grid(row=row, column=1, sticky="ew", pady=2)
        btn = ttk.Button(parent, text="Paste", width=6, command=lambda e=ent: self._paste_into(e))
        btn.grid(row=row, column=2, sticky="w", padx=(4, 0))
        add_tooltip(lbl, tooltip)
        add_tooltip(ent, tooltip)
        add_tooltip(btn, "Paste from clipboard into this field.")

    def _adv_entry(self, parent, row, label, var, tooltip: str):
        lbl = ttk.Label(parent, text=label + ":")
        lbl.grid(row=row, column=0, sticky="w", pady=1)
        ent = ttk.Entry(parent, textvariable=var)
        ent.grid(row=row, column=1, sticky="ew", pady=1)
        btn = ttk.Button(parent, text="Paste", width=6, command=lambda e=ent: self._paste_into(e))
        btn.grid(row=row, column=2, sticky="w", padx=(4, 0))
        add_tooltip(lbl, tooltip)
        add_tooltip(ent, tooltip)
        add_tooltip(btn, "Paste from clipboard into this field.")

    def _paste_into(self, entry: ttk.Entry):
        try:
            text = self.clipboard_get()
        except tk.TclError:
            text = ""
        if text:
            entry.delete(0, tk.END)
            entry.insert(0, text)

    def _open_log_folder(self):
        folder = LOG_PATH.parent
        try:
            os.startfile(str(folder))
        except Exception:
            try:
                subprocess.Popen(["explorer", str(folder)])
            except Exception as exc:
                messagebox.showerror(PROJECT_NAME, f"Could not open log folder:\n{exc}")

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
        )
        return cfg

    def _run_task(self, description: str, func):
        if self._task_running:
            messagebox.showinfo(PROJECT_NAME, "Another operation is already in progress.")
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
                if error:
                    messagebox.showerror(PROJECT_NAME, f"Operation failed:\n{error}")
                else:
                    messagebox.showinfo(PROJECT_NAME, "Operation completed successfully.")

            self.after(0, done)

        threading.Thread(target=worker, daemon=True).start()

    def _on_install(self):
        cfg = self._build_config()
        set_debug_logging(cfg.debug_logging)
        autostart.set_autostart(self.var_autostart.get())

        def task():
            install_or_update_agent_and_service(cfg)
            cfg.save()
            self.config_obj = cfg
            shortcut_mod.ensure_start_menu_shortcut()

        self._run_task("Installing agent and configuring service...", task)

    def _on_update_agent(self):
        current = get_agent_version()
        target = _parse_download_version()
        if current not in ("Not installed", "Unknown") and target and current == target:
            if not messagebox.askyesno(
                PROJECT_NAME,
                f"Agent is already at version {current}.\nForce re-download and restart?",
            ):
                return

        def task():
            update_agent_only()

        self._run_task("Updating agent binary...", task)

    def _on_apply(self):
        cfg = self._build_config()
        set_debug_logging(cfg.debug_logging)
        autostart.set_autostart(self.var_autostart.get())

        def task():
            apply_configuration_only(cfg)
            cfg.save()
            self.config_obj = cfg
            shortcut_mod.ensure_start_menu_shortcut()

        self._run_task("Applying configuration to service...", task)

    def _on_uninstall(self):
        if not messagebox.askyesno(
            PROJECT_NAME,
            "This will stop and remove the service, firewall rule, scheduled task and agent files. Continue?",
        ):
            return

        from shutil import rmtree

        def task():
            delete_update_task()
            delete_service()
            remove_firewall_rule()
            try:
                if AGENT_DIR.exists():
                    rmtree(AGENT_DIR)
            except Exception as exc:
                log(f"Failed to remove agent dir: {exc}")
            shortcut_mod.remove_start_menu_shortcut()
            try:
                if LOCK_PATH.exists():
                    LOCK_PATH.unlink()
            except Exception as exc:
                log(f"Failed to remove lock file on uninstall: {exc}")

        self._run_task("Uninstalling agent and cleaning up...", task)

    def _on_start_service(self):
        def worker():
            try:
                start_service()
            finally:
                self.after(0, self._update_status)
        threading.Thread(target=worker, daemon=True).start()

    def _on_stop_service(self):
        def worker():
            try:
                stop_service()
            finally:
                self.after(0, self._update_status)
        threading.Thread(target=worker, daemon=True).start()

    def _on_restart_service(self):
        def worker():
            try:
                restart_service()
            finally:
                self.after(0, self._update_status)
        threading.Thread(target=worker, daemon=True).start()

    def _open_hub_url(self):
        url = self.var_hub_url.get().strip()
        if not url and self.config_obj:
            url = (self.config_obj.hub_url or "").strip()
        if not url:
            messagebox.showinfo(PROJECT_NAME, "Hub URL is not configured.")
            return
        if not url.startswith("http://") and not url.startswith("https://"):
            url = "https://" + url
        try:
            webbrowser.open(url)
        except Exception as exc:
            messagebox.showerror(PROJECT_NAME, f"Failed to open hub URL:\n{exc}")

    def _update_status(self):
        status = get_service_status()
        self.label_status.config(text=f"Service status: {status}")
        version = get_agent_version()
        self.label_version.config(text=f"Agent version: {version}")

        hub = check_hub_status(self.var_hub_url.get().strip() or (self.config_obj.hub_url if self.config_obj else ""))
        self.label_hub_status.config(text=f"Hub: {hub}")

        self._update_tray_title(status)
        if status in ("START_PENDING", "STOP_PENDING"):
            self.after(1000, self._update_status)

    def _on_about(self):
        messagebox.showinfo(
            PROJECT_NAME,
            f"{PROJECT_NAME} v{APP_VERSION}\n\n"
            "Beszel agent installer and manager.\n\n"
            "Credit: Verhoef",
        )

    def _on_about_beszel(self):
        messagebox.showinfo(
            "About Beszel",
            "Beszel is an open-source monitoring hub and agent.\n\n"
            "Created by henrygd (GitHub).\n\n"
            "Project: https://github.com/henrygd/beszel\n"
            "Website: https://beszel.dev",
        )

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
            pystray.MenuItem("Open BeszelAgentManager", self._tray_open, default=True),
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


def main():
    app = BeszelAgentManagerApp()
    app.mainloop()
