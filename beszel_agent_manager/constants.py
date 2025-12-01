from __future__ import annotations
import os
from pathlib import Path

PROJECT_NAME = "BeszelAgentManager"
APP_VERSION = "1.8.0"

AGENT_SERVICE_NAME = PROJECT_NAME
AGENT_DISPLAY_NAME = PROJECT_NAME

PROGRAM_FILES = os.environ.get("ProgramFiles", r"C:\Program Files")
AGENT_DIR = Path(PROGRAM_FILES) / "Beszel-Agent"
AGENT_EXE_NAME = "beszel-agent.exe"
AGENT_EXE_PATH = AGENT_DIR / AGENT_EXE_NAME

PROGRAM_DATA = Path(os.environ.get("ProgramData", r"C:\ProgramData"))
DATA_DIR = PROGRAM_DATA / PROJECT_NAME
CONFIG_PATH = DATA_DIR / "config.json"
LOG_PATH = DATA_DIR / "manager.log"
UPDATE_SCRIPT_PATH = DATA_DIR / "update-beszel-agent.ps1"

AUTO_UPDATE_TASK_NAME = PROJECT_NAME + "Update"

AGENT_DOWNLOAD_URL = (
    "https://github.com/henrygd/beszel/releases/download/"
    "v0.16.1/beszel-agent_windows_amd64.zip"
)

NSSM_DOWNLOAD_URL = "https://www.nssm.cc/release/nssm-2.24.zip"
NSSM_DIR = DATA_DIR / "nssm"
NSSM_EXE_PATH = NSSM_DIR / "nssm.exe"

DEFAULT_LISTEN_PORT = 45876
FIREWALL_RULE_NAME = "Beszel Agent"

LOCK_PATH = DATA_DIR / "instance.lock"
