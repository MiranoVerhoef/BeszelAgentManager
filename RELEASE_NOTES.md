## Summary

- Bumped manager, helper, and installers to 4.0.10.
- Hardened LocalSystem configuration validation and blocked unsafe process-environment overrides.
- Moved service logs, agent logs, runtime state, update staging, and broker policy into protected service-owned storage.
- Hardened named-pipe creation and verified the server belongs to the LocalSystem background service before sending requests.
- Blocked silent broker downgrades for the manager and agent.
- Changed unsigned manager updates to open the official GitHub release page for manual installation with Windows administrator approval.
- Kept legacy logs available without privileged copying and removed non-allowlisted variables from the installed agent service.
- Thanks @mews-se for the responsible disclosure.
