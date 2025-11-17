from __future__ import annotations
from dataclasses import dataclass, asdict
import json
from typing import Dict, Any
from .constants import CONFIG_PATH, DEFAULT_LISTEN_PORT
from .util import ensure_data_dir, log

@dataclass
class AgentConfig:
    key: str = ""
    token: str = ""
    hub_url: str = ""
    listen: int = DEFAULT_LISTEN_PORT

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

    auto_update_enabled: bool = False
    update_interval_days: int = 1
    last_known_version: str = ""
    debug_logging: bool = False

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "AgentConfig":
        kwargs: Dict[str, Any] = {}
        for field in cls.__dataclass_fields__.values():  # type: ignore[attr-defined]
            if field.name in data:
                kwargs[field.name] = data[field.name]
        return cls(**kwargs)

    def save(self) -> None:
        ensure_data_dir()
        with CONFIG_PATH.open("w", encoding="utf-8") as f:
            json.dump(self.to_dict(), f, indent=2)
        log("Saved configuration.")

    @classmethod
    def load(cls) -> "AgentConfig":
        ensure_data_dir()
        if not CONFIG_PATH.exists():
            log("No config file found, using defaults.")
            return cls()
        try:
            with CONFIG_PATH.open("r", encoding="utf-8") as f:
                data = json.load(f)
            cfg = cls.from_dict(data)
            log("Loaded configuration from disk.")
            return cfg
        except Exception as exc:
            log(f"Failed to load config, using defaults. Error: {exc}")
            return cls()
