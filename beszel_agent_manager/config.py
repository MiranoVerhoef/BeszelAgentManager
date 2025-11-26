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

    debug_logging: bool = False

    # Default behavior: start hidden when launched with Windows (tray only)
    start_hidden: bool = True

    # Internal flag so the GUI shows only on the very first run
    first_run_done: bool = False

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
                return cls(**kwargs)
            except Exception:
                return cls()
        return cls()

    def save(self) -> None:
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        data = dataclasses.asdict(self)
        with CONFIG_PATH.open("w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
