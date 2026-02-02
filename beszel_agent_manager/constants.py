from __future__ import annotations
import os
from pathlib import Path

PROJECT_NAME = "BeszelAgentManager"
APP_VERSION = "2.8.5"

AGENT_SERVICE_NAME = "Beszel Agent"
AGENT_DISPLAY_NAME = "Beszel Agent"

PROGRAM_FILES = os.environ.get("ProgramFiles", r"C:\Program Files")
MANAGER_EXE_PATH = Path(PROGRAM_FILES) / PROJECT_NAME / f"{PROJECT_NAME}.exe"
AGENT_DIR = Path(PROGRAM_FILES) / "Beszel-Agent"
AGENT_EXE_NAME = "beszel-agent.exe"
AGENT_EXE_PATH = AGENT_DIR / AGENT_EXE_NAME

PROGRAM_DATA = Path(os.environ.get("ProgramData", r"C:\ProgramData"))
DATA_DIR = PROGRAM_DATA / PROJECT_NAME
CONFIG_PATH = DATA_DIR / "config.json"
LOG_PATH = DATA_DIR / "manager.log"

MANAGER_LOG_ARCHIVE_DIR = DATA_DIR / "manager_logs"
MANAGER_LOG_LAST_DATE_PATH = DATA_DIR / "manager_log_last_date.txt"

SUPPORT_BUNDLES_DIR = DATA_DIR / "support_bundles"

MANAGER_PREVIOUS_EXE_PATH = Path(PROGRAM_FILES) / PROJECT_NAME / f"{PROJECT_NAME}.previous.exe"

AGENT_STAGED_EXE_PATH = AGENT_DIR / "beszel-agent.new.exe"

AGENT_LOG_DIR = DATA_DIR / "agent_logs"
AGENT_LOG_CURRENT_PATH = AGENT_LOG_DIR / "beszel-agent.log"
AGENT_LOG_ROTATE_TASK_NAME = PROJECT_NAME + "AgentLogRotate"
UPDATE_SCRIPT_PATH = DATA_DIR / "update-beszel-agent.ps1"

AUTO_UPDATE_TASK_NAME = PROJECT_NAME + "Update"

AUTO_RESTART_TASK_NAME = PROJECT_NAME + "RestartService"

AGENT_DOWNLOAD_URL = (
    "https://github.com/henrygd/beszel/releases/download/"
    "v0.16.1/beszel-agent_windows_amd64.zip"
)

NSSM_DOWNLOAD_URL = "https://www.nssm.cc/release/nssm-2.24.zip"
NSSM_DIR = DATA_DIR / "nssm"
NSSM_EXE_PATH = NSSM_DIR / "nssm.exe"

DEFAULT_LISTEN_PORT = 45876
FIREWALL_RULE_NAME = "Beszel Agent"


MANAGER_REPO = "MiranoVerhoef/BeszelAgentManager"

MANAGER_ASSET_NAME = f"{PROJECT_NAME}.exe"

MANAGER_UPDATES_DIR = DATA_DIR / "updates"

MANAGER_UPDATE_SCRIPT = DATA_DIR / "update-manager.ps1"

LOCK_PATH = DATA_DIR / "instance.lock"
