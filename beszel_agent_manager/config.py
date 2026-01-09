from __future__ import annotations
import json
import dataclasses
from dataclasses import dataclass
from .constants import CONFIG_PATH, DATA_DIR


@dataclass
class AgentConfig:
    key: str = ""
    token: str = ""
    hub_url: str = ""
    # Optional IP/URL fallback used if DNS resolution for hub_url fails.
    hub_url_ip_fallback: str = ""
    # Enable/disable automatic switching to hub_url_ip_fallback when DNS fails.
    hub_url_ip_fallback_enabled: bool = False
    # How often (seconds) the manager checks primary hub reachability for failover.
    hub_url_ip_fallback_check_interval_seconds: int = 15
    # Consecutive failures needed before switching to fallback.
    hub_url_ip_fallback_failures_to_failover: int = 2
    # Consecutive successes needed before switching back to primary.
    hub_url_ip_fallback_successes_to_restore: int = 2
    listen: int | None = None

    data_dir: str = ""
    docker_host: str = ""
    exclude_containers: str = ""
    exclude_smart: str = ""
    extra_filesystems: str = ""
    filesystem: str = ""
    intel_gpu_device: str = ""
    key_file: str = ""
    token_file: str = ""
    lhm: str = ""
    log_level: str = ""
    mem_calc: str = ""
    network: str = ""
    nics: str = ""
    sensors: str = ""
    primary_sensor: str = ""
    sys_sensors: str = ""
    service_patterns: str = ""
    smart_devices: str = ""
    system_name: str = ""
    skip_gpu: str = ""

    auto_update_enabled: bool = True
    update_interval_days: int = 1
    last_known_version: str = ""

    auto_restart_enabled: bool = False
    auto_restart_interval_hours: int = 24

    debug_logging: bool = False

    # Default behavior: start hidden when launched with Windows (tray only)
    start_hidden: bool = True

    # Internal flag so the GUI shows only on the very first run
    first_run_done: bool = False

    # Manager update notifications
    manager_update_notify_enabled: bool = True
    manager_update_check_interval_hours: int = 6
    manager_update_skip_version: str = ""
    manager_update_tray_badge_enabled: bool = True
    # If enabled, manager update checks/version picker will include GitHub pre-releases.
    manager_update_include_prereleases: bool = False

    # Tracks the last configuration that was applied to the Windows service (env/tasks).
    # Used to avoid unnecessary Apply operations (service restarts) when nothing changed.
    last_applied_fingerprint: str = ""
    last_applied_at: str = ""

    def _apply_relevant_dict(self) -> dict:
        """Subset of settings that require Apply settings (service + scheduled tasks).

        Manager-only settings (UI, update notifications, etc.) are intentionally excluded.
        """
        keys = [
            # Core agent env
            "key",
            "token",
            "hub_url",
            "listen",

            # Agent advanced env
            "data_dir",
            "docker_host",
            "exclude_containers",
            "exclude_smart",
            "extra_filesystems",
            "filesystem",
            "intel_gpu_device",
            "key_file",
            "token_file",
            "lhm",
            "log_level",
            "mem_calc",
            "network",
            "nics",
            "sensors",
            "primary_sensor",
            "sys_sensors",
            "service_patterns",
            "smart_devices",
            "system_name",
            "skip_gpu",

            # Scheduled tasks
            "auto_update_enabled",
            "update_interval_days",
            "auto_restart_enabled",
            "auto_restart_interval_hours",
        ]
        d: dict[str, object] = {}
        for k in keys:
            d[k] = getattr(self, k)
        return d

    def apply_fingerprint(self) -> str:
        import hashlib
        import json

        payload = json.dumps(self._apply_relevant_dict(), sort_keys=True, separators=(",", ":"), ensure_ascii=True)
        return hashlib.sha256(payload.encode("utf-8")).hexdigest()

    @classmethod
    def load(cls) -> "AgentConfig":
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        if CONFIG_PATH.exists():
            try:
                with CONFIG_PATH.open("r", encoding="utf-8") as f:
                    raw = json.load(f)
                kwargs: dict[str, object] = {}
                for fld in dataclasses.fields(cls):
                    kwargs[fld.name] = raw.get(fld.name, fld.default)

                # Backward-compat: if older configs had a fallback value set but no enable flag,
                # default to enabled so behavior doesn't silently change after upgrade.
                if (
                    "hub_url_ip_fallback_enabled" not in raw
                    and str(raw.get("hub_url_ip_fallback", "") or "").strip() != ""
                ):
                    kwargs["hub_url_ip_fallback_enabled"] = True
                return cls(**kwargs)
            except Exception:
                return cls()
        return cls()

    def save(self) -> None:
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        data = dataclasses.asdict(self)
        with CONFIG_PATH.open("w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
