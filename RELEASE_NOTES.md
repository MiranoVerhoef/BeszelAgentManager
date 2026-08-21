## Summary

- Bumped version to 4.0.8.
- Added Beszel v0.18.8 agent variables `ALL_PROXY` and `EXIT_ON_DNS_ERROR`.
- Added previously missing `DOCKER_TIMEOUT` and `SMART_DEVICES_SEPARATOR` variables.
- Updated `GPU_COLLECTOR` and `SMART_DEVICES` guidance for current collectors and explicit device-type hints.
- Added official SHA-256 checksum verification for every Beszel Agent install, update, rollback, and scheduled update.
- Blocked conflicting use of `EXIT_ON_DNS_ERROR` and manager WebSocket offline backoff.
- Redacted credential-bearing `ALL_PROXY` values from logs and support bundles.

## Impact

Agent archives are installed only when the selected official GitHub release contains both `beszel-agent_windows_amd64.zip` and its versioned checksum file, and the archive hash matches exactly. A missing or mismatched checksum blocks installation before extraction or replacement of the installed agent.

`EXIT_ON_DNS_ERROR` and manager WebSocket offline backoff are alternative retry strategies. The UI and privileged broker reject configurations that enable both.
