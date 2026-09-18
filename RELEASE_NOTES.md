## Summary

- Bumped manager, helper, and installers to 4.0.10.
- Hardened LocalSystem configuration validation and blocked unsafe process-environment overrides.
- Moved service logs, agent logs, runtime state, update staging, and broker policy into protected service-owned storage.
- Hardened named-pipe creation and verified the server belongs to the LocalSystem background service before sending requests.
- Added automatic recovery when another process temporarily holds the broker pipe name.
- Migrated legacy `TOKEN_FILE`, `KEY_FILE`, and `DATA_DIR` settings in the signed-in user's context, with a visible warning when migration cannot complete.
- Added Beszel 0.19 variables `CA_CERT_FILE` and `ZFS_INTERVAL`, requiring certificate files to use an absolute local path.
- Rejected control characters in every active environment value before NSSM is changed.
- Blocked silent broker downgrades for the manager and agent.
- Changed unsigned manager updates to open the official GitHub release page for manual installation with Windows administrator approval.
- Kept legacy logs available without privileged copying and removed non-allowlisted variables from the installed agent service.
- Thanks @mews-se for the responsible disclosure.
