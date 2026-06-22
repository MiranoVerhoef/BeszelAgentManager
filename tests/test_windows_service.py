from __future__ import annotations

import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from beszel_agent_manager import windows_service as service


def completed(cmd: list[str], returncode: int = 0, stdout: str = "", stderr: str = ""):
    return subprocess.CompletedProcess(cmd, returncode, stdout, stderr)


class ServiceConfigurationTests(unittest.TestCase):
    def test_redundant_display_name_setting_is_omitted(self) -> None:
        settings = dict(service._desired_service_settings({}))
        self.assertNotIn("DisplayName", settings)

    def test_different_display_name_is_preserved(self) -> None:
        with patch.object(service, "AGENT_DISPLAY_NAME", "Beszel Agent Service"):
            settings = dict(service._desired_service_settings({}))
        self.assertEqual(settings["DisplayName"], ["Beszel Agent Service"])

    def test_retries_transient_service_manager_error(self) -> None:
        command = ["nssm.exe", "set", "Beszel Agent", "Start", "SERVICE_AUTO_START"]
        results = [
            completed(command, 3, stderr="The specified service has been marked for deletion."),
            completed(command),
        ]
        with patch.object(service, "run", side_effect=results) as run_mock, patch.object(service.time, "sleep"):
            service._run_service_command(command, "Set start mode")
        self.assertEqual(run_mock.call_count, 2)

    def test_apply_skips_setting_that_is_already_correct(self) -> None:
        snapshots: dict[str, list[str] | None] = {}
        changed: list[str] = []
        with patch.object(service, "_get_nssm_parameter", return_value=["1500"]), patch.object(
            service, "_write_nssm_parameter"
        ) as write_mock:
            service._apply_nssm_parameter(
                "nssm.exe",
                "AppStopMethodConsole",
                ["1500"],
                snapshots,
                changed,
            )
        write_mock.assert_not_called()
        self.assertEqual(changed, [])

    def test_apply_verifies_changed_setting(self) -> None:
        snapshots: dict[str, list[str] | None] = {}
        changed: list[str] = []
        with patch.object(service, "_get_nssm_parameter", side_effect=[["1000"], ["1500"]]), patch.object(
            service, "_write_nssm_parameter"
        ) as write_mock:
            service._apply_nssm_parameter(
                "nssm.exe",
                "AppStopMethodConsole",
                ["1500"],
                snapshots,
                changed,
            )
        write_mock.assert_called_once()
        self.assertEqual(changed, ["AppStopMethodConsole"])

    def test_apply_rejects_failed_readback(self) -> None:
        snapshots: dict[str, list[str] | None] = {}
        changed: list[str] = []
        with patch.object(service, "_get_nssm_parameter", side_effect=[["1000"], ["1000"]]), patch.object(
            service, "_write_nssm_parameter"
        ):
            with self.assertRaises(service.ServiceError):
                service._apply_nssm_parameter(
                    "nssm.exe",
                    "AppStopMethodConsole",
                    ["1500"],
                    snapshots,
                    changed,
                )
        self.assertEqual(changed, ["AppStopMethodConsole"])

    def test_new_service_is_removed_when_configuration_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            agent_path = Path(temp_dir) / "beszel-agent.exe"
            agent_path.write_bytes(b"test")
            service_exists = [False, False, False]

            def exists(name: str) -> bool:
                if name == service.LEGACY_AGENT_SERVICE_NAME:
                    return False
                return service_exists.pop(0) if service_exists else True

            with (
                patch.object(service, "AGENT_EXE_PATH", agent_path),
                patch.object(service, "AGENT_DIR", Path(temp_dir)),
                patch.object(service, "_find_nssm", return_value="nssm.exe"),
                patch.object(service, "_service_exists", side_effect=exists),
                patch.object(service, "get_service_status", return_value="NOT FOUND"),
                patch.object(service, "_ensure_service_uses_nssm_path"),
                patch.object(service, "_require_service_exists"),
                patch.object(service, "_run_service_command"),
                patch.object(service, "_wait_until_service_exists", return_value=True),
                patch.object(service, "_ensure_agent_log_dir"),
                patch.object(service, "_apply_nssm_parameter", side_effect=service.ServiceError("failed")),
                patch.object(service, "_remove_new_service") as remove_mock,
                patch.object(service, "restart_service"),
            ):
                with self.assertRaises(service.ServiceError):
                    service.create_or_update_service({})
            remove_mock.assert_called_once_with("nssm.exe")

    def test_existing_service_settings_are_rolled_back_on_failure(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            agent_path = Path(temp_dir) / "beszel-agent.exe"
            agent_path.write_bytes(b"test")
            snapshots = {"Application": [r"C:\old\agent.exe"]}
            changed = ["Application"]

            def fail_apply(_nssm, _parameter, _desired, actual_snapshots, actual_changed):
                actual_snapshots.update(snapshots)
                actual_changed.extend(changed)
                raise service.ServiceError("failed")

            def exists(name: str) -> bool:
                return name == service.AGENT_SERVICE_NAME

            with (
                patch.object(service, "AGENT_EXE_PATH", agent_path),
                patch.object(service, "AGENT_DIR", Path(temp_dir)),
                patch.object(service, "_find_nssm", return_value="nssm.exe"),
                patch.object(service, "_service_exists", side_effect=exists),
                patch.object(service, "get_service_status", return_value="RUNNING"),
                patch.object(service, "_ensure_service_uses_nssm_path"),
                patch.object(service, "_require_service_exists"),
                patch.object(service, "_ensure_agent_log_dir"),
                patch.object(service, "_apply_nssm_parameter", side_effect=fail_apply),
                patch.object(service, "_rollback_nssm_parameters") as rollback_mock,
                patch.object(service, "restart_service"),
            ):
                with self.assertRaises(service.ServiceError):
                    service.create_or_update_service({})
            rollback_mock.assert_called_once_with("nssm.exe", snapshots, changed)


if __name__ == "__main__":
    unittest.main()
