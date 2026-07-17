## Summary

- Bumped version to 4.0.7.
- Fixed GUI updates reopening before Inno Setup completed.
- Added a secured per-update completion marker between the SYSTEM installer and user relauncher.
- Forced replacement of manager version metadata and the background helper during upgrades.
- Added post-install verification for the GUI, helper, and VERSION metadata.
- Added a persistent silent-update setup log under ProgramData.
- Added optional WebSocket offline backoff for devices that cannot always reach their Hub.
- Pauses the agent after 12 consecutive WebSocket failures and retries after 1, 2, 5, 10, 30, then 60 minutes.
- Restores normal agent operation immediately after a successful WebSocket connection.
- Detects Windows network-address changes and requests an immediate debounced retry.
- Reworked Extra into a compact two-column card layout.

## Impact

Manager updates now reopen only after the complete installer process tree exits. Mixed installations containing a new GUI with old metadata or an old background helper are rejected instead of reporting a successful update.

WebSocket offline backoff is disabled by default and can be enabled under Extra. Only new agent log events are considered after enabling it. Scheduled agent updates and periodic restarts are deferred while the manager has paused the agent. Network changes bypass the current delay once after a three-second trailing debounce, so the retry starts after adapters settle.
