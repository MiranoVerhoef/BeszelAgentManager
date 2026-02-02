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


    env_active_names: list[str] = dataclasses.field(default_factory=list)
    auto_update_enabled: bool = True
    update_interval_days: int = 1
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

                if (
                    "hub_url_ip_fallback_enabled" not in raw
                    and str(raw.get("hub_url_ip_fallback", "") or "").strip() != ""
                ):
                    kwargs["hub_url_ip_fallback_enabled"] = True

                if "env_enabled" not in raw:
                    core = {"key", "token", "hub_url", "hub_url_ip_fallback", "hub_url_ip_fallback_enabled"}
                    if raw.get("env_active_names"):
                        kwargs["env_enabled"] = True
                    any_env = False
                    for fld in dataclasses.fields(cls):
                        if fld.name in core:
                            continue
                        v = raw.get(fld.name, "")
                        if isinstance(v, str) and v.strip() != "":
                            any_env = True
                            break
                    if any_env:
                        kwargs["env_enabled"] = True

                if "env_active_names" not in raw:
                    active: list[str] = []
                    env_tables = raw.get("env_tables")
                    if isinstance(env_tables, dict):
                        for k, v in env_tables.items():
                            if isinstance(k, str) and k in {f.name for f in dataclasses.fields(cls)}:
                                if isinstance(v, str):
                                    kwargs[k] = v
                                active.append(k)
                        if active:
                            kwargs["env_enabled"] = True
                    elif isinstance(env_tables, list):
                        for item in env_tables:
                            if isinstance(item, dict):
                                k = item.get("name") or item.get("key")
                                v = item.get("value")
                                if isinstance(k, str) and k in {f.name for f in dataclasses.fields(cls)}:
                                    if isinstance(v, str):
                                        kwargs[k] = v
                                    active.append(k)
                        if active:
                            kwargs["env_enabled"] = True

                    if not active:
                        core = {"key", "token", "hub_url", "hub_url_ip_fallback", "hub_url_ip_fallback_enabled", "env_enabled", "env_active_names"}
                        for fld in dataclasses.fields(cls):
                            if fld.name in core:
                                continue
                            v = raw.get(fld.name, "")
                            if isinstance(v, str) and v.strip() != "":
                                active.append(fld.name)
                    kwargs["env_active_names"] = active
                return cls(**kwargs)
            except Exception:
                return cls()
        return cls()

    def save(self) -> None:
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        data = dataclasses.asdict(self)
        with CONFIG_PATH.open("w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
