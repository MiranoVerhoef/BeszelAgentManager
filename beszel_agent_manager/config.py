from __future__ import annotations
import json
import dataclasses
from dataclasses import dataclass
from .constants import CONFIG_PATH, DATA_DIR


@dataclass
class CustomEnvVar:
    name: str = ""
    value: str = ""


@dataclass
class AgentConfig:
    key: str = ""
    token: str = ""
    hub_url: str = ""
    hub_url_ip_fallback: str = ""
    hub_url_ip_fallback_enabled: bool = False
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
    nvml: str = ""
    smart_interval: str = ""
    disk_usage_cache: str = ""
    skip_systemd: str = ""

    env_enabled: bool = True

    # Preset env rows enabled in the UI (names like DATA_DIR, DOCKER_HOST, ...)
    env_active_names: list[str] = dataclasses.field(default_factory=list)

    # Custom env rows (arbitrary NAME=VALUE pairs)
    env_custom: list[CustomEnvVar] = dataclasses.field(default_factory=list)

    auto_update_enabled: bool = True
    # Interval in hours (default 24)
    update_interval_hours: int = 24
    # Tracks last scheduled auto-update run (ISO string)
    last_agent_auto_update_at: str = ""

    last_known_version: str = ""

    auto_restart_enabled: bool = False
    auto_restart_interval_hours: int = 24

    debug_logging: bool = False

    start_hidden: bool = True

    first_run_done: bool = False

    github_token_enc: str = ""

    manager_update_notify_enabled: bool = True
    manager_update_check_interval_hours: int = 6
    manager_update_skip_version: str = ""
    manager_update_tray_badge_enabled: bool = True
    manager_update_include_prereleases: bool = False

    last_applied_fingerprint: str = ""
    last_applied_at: str = ""

    def _apply_relevant_dict(self) -> dict:
        keys = [
            "key",
            "token",
            "hub_url",
            "listen",

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
            "nvml",
            "smart_interval",
            "disk_usage_cache",
            "skip_systemd",

            "hub_url_ip_fallback",
            "hub_url_ip_fallback_enabled",

            "env_enabled",
            "env_active_names",
            "env_custom",

            "auto_update_enabled",
            "update_interval_hours",

            "auto_restart_enabled",
            "auto_restart_interval_hours",
        ]
        d: dict[str, object] = {}
        for k in keys:
            d[k] = getattr(self, k)
        return d

    def apply_fingerprint(self) -> str:
        import hashlib
        payload = json.dumps(
            self._apply_relevant_dict(),
            sort_keys=True,
            separators=(",", ":"),
            ensure_ascii=True,
            default=lambda o: dataclasses.asdict(o) if dataclasses.is_dataclass(o) else str(o),
        )
        return hashlib.sha256(payload.encode("utf-8")).hexdigest()

    @classmethod
    def load(cls) -> "AgentConfig":
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        if not CONFIG_PATH.exists():
            return cls()

        try:
            with CONFIG_PATH.open("r", encoding="utf-8") as f:
                raw = json.load(f)
        except Exception:
            return cls()

        try:
            kwargs: dict[str, object] = {}
            for fld in dataclasses.fields(cls):
                if fld.name in raw:
                    kwargs[fld.name] = raw.get(fld.name)
                else:
                    if fld.default is not dataclasses.MISSING:
                        kwargs[fld.name] = fld.default
                    elif fld.default_factory is not dataclasses.MISSING:  # type: ignore
                        kwargs[fld.name] = fld.default_factory()  # type: ignore
                    else:
                        kwargs[fld.name] = None

            # Hub fallback enabled migration
            if (
                "hub_url_ip_fallback_enabled" not in raw
                and str(raw.get("hub_url_ip_fallback", "") or "").strip() != ""
            ):
                kwargs["hub_url_ip_fallback_enabled"] = True

            # Days -> hours migration
            if "update_interval_hours" not in raw:
                days = raw.get("update_interval_days")
                try:
                    days_i = int(days)
                except Exception:
                    days_i = 1
                if days_i < 1:
                    days_i = 1
                kwargs["update_interval_hours"] = days_i * 24

            # env_custom parse
            env_custom_raw = raw.get("env_custom")
            custom_list: list[CustomEnvVar] = []
            if isinstance(env_custom_raw, list):
                for item in env_custom_raw:
                    if isinstance(item, dict):
                        n = str(item.get("name") or "").strip()
                        v = str(item.get("value") or "")
                        if n:
                            custom_list.append(CustomEnvVar(name=n, value=v))
            kwargs["env_custom"] = custom_list

            # env_active_names migration
            if "env_active_names" not in raw:
                active: list[str] = []
                env_tables = raw.get("env_tables")
                if isinstance(env_tables, list):
                    for item in env_tables:
                        if isinstance(item, dict):
                            k = item.get("name") or item.get("key")
                            if isinstance(k, str):
                                active.append(k)
                elif isinstance(env_tables, dict):
                    for k in env_tables.keys():
                        if isinstance(k, str):
                            active.append(k)

                if not active:
                    mapping = {
                        "DATA_DIR": "data_dir",
                        "DOCKER_HOST": "docker_host",
                        "EXCLUDE_CONTAINERS": "exclude_containers",
                        "EXCLUDE_SMART": "exclude_smart",
                        "EXTRA_FILESYSTEMS": "extra_filesystems",
                        "FILESYSTEM": "filesystem",
                        "INTEL_GPU_DEVICE": "intel_gpu_device",
                        "NVML": "nvml",
                        "KEY_FILE": "key_file",
                        "TOKEN_FILE": "token_file",
                        "LHM": "lhm",
                        "LOG_LEVEL": "log_level",
                        "MEM_CALC": "mem_calc",
                        "NETWORK": "network",
                        "NICS": "nics",
                        "SENSORS": "sensors",
                        "PRIMARY_SENSOR": "primary_sensor",
                        "SYS_SENSORS": "sys_sensors",
                        "SERVICE_PATTERNS": "service_patterns",
                        "SMART_DEVICES": "smart_devices",
                        "SMART_INTERVAL": "smart_interval",
                        "SYSTEM_NAME": "system_name",
                        "SKIP_GPU": "skip_gpu",
                        "DISK_USAGE_CACHE": "disk_usage_cache",
                        "SKIP_SYSTEMD": "skip_systemd",
                    }
                    for env_name, attr in mapping.items():
                        v = raw.get(attr, "")
                        if isinstance(v, str) and v.strip() != "":
                            active.append(env_name)
                kwargs["env_active_names"] = active

            if "env_enabled" not in raw:
                kwargs["env_enabled"] = True

            return cls(**kwargs)  # type: ignore[arg-type]
        except Exception:
            return cls()

    def save(self) -> None:
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        data = dataclasses.asdict(self)
        with CONFIG_PATH.open("w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
