## Summary

- Bumped version to 4.0.8.
- Added Beszel v0.18.8 agent variables `ALL_PROXY` and `EXIT_ON_DNS_ERROR`.
- Added previously missing `DOCKER_TIMEOUT` and `SMART_DEVICES_SEPARATOR` variables.
- Updated `GPU_COLLECTOR` and `SMART_DEVICES` guidance for current collectors and explicit device-type hints.
- Added official SHA-256 checksum verification for every Beszel Agent install, update, rollback, and scheduled update.
- Blocked conflicting use of `EXIT_ON_DNS_ERROR` and manager WebSocket offline backoff.
- Redacted credential-bearing `ALL_PROXY` values from logs and support bundles.
- Added Environment-table search by variable name or description.
- Made initial and minimum window sizing DPI-aware while retaining low-resolution fitting.
- Added SHA-256-aware incremental installer updates that retain unchanged files, replace changed or missing files, remove obsolete manifest-owned files, and verify the final installation.

## Impact

Agent archives are installed only when the selected official GitHub release contains both `beszel-agent_windows_amd64.zip` and its versioned checksum file, and the archive hash matches exactly. A missing or mismatched checksum blocks installation before extraction or replacement of the installed agent.

`EXIT_ON_DNS_ERROR` and manager WebSocket offline backoff are alternative retry strategies. The UI and privileged broker reject configurations that enable both.

Bundled and Lite builds now include separate application-file manifests. Normal upgrades avoid rewriting unchanged application files; v3 migration, rollback, and incomplete layouts still use a full refresh.
