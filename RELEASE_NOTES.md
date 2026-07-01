# BeszelAgentManager v4.0.0 RC1

Version 4 replaces the Python/Tk application with a native .NET 10 and WinUI 3 desktop application plus a secured LocalSystem background service.

## Highlights

- Native WinUI interface, tray integration, and hidden startup
- No repeated UAC prompts for routine agent management
- Versioned named-pipe broker restricted to the installing Windows account
- Background-service scheduling instead of Windows Scheduled Tasks
- Automatic DNS fallback with five-check primary recovery
- Agent and manager upgrade, rollback, and force-reinstall selection
- Checksum-verified silent manager updates
- Optional update notifications, prereleases, skipped versions, and tray state
- Optional Defender exclusion and third-party antivirus allowlist instructions

## Scheduling

- Automatic agent updates from 1 to 720 hours
- Periodic agent restarts in minutes or hours
- Daily agent-log rotation at 00:05 local time
- Persistent last-run and next-due state across service restart and reboot

## Migration

- Preserves v3.1.0 configuration, token ciphertext, logs, agent environment, and service state
- Migrates legacy autostart while preserving hidden/visible behavior
- Moves the agent service to a stable, quoted ProgramData NSSM path
- Removes obsolete scheduled tasks and Python application files

## Security and reliability

- Caller SID verification and allowlisted broker actions
- Serialized privileged mutations
- Restricted ProgramData and privileged staging ACLs
- Reparse-point rejection before recursive privileged cleanup
- Bounded GitHub downloads and validated release asset URLs
- Final-state service completion checks
- Background-service crash recovery
- Clean install, v3 upgrade, v4 replacement, uninstall, DNS recovery, and reboot validation

## Release status

This release remains an RC until the GitHub Actions pull-request installer artifact completes the final VM validation matrix.
