# BeszelAgentManager v4.0.4

## Installer editions

- Adds a Bundled installer containing .NET 10 for the easiest setup.
- Adds a smaller Lite installer for systems with Microsoft .NET 10 Desktop Runtime x64 already installed.
- Lite setup detects a missing runtime, offers to open the official Microsoft download page, and waits for the runtime before installation.
- Manager updates preserve the installed standard or Lite edition; only Lite adds an edition label beside the version number.
- Release checksums cover both fixed, allowlisted installer filenames.

## Update reliability

- Includes the stale-relauncher fix so an update cannot reopen an older manager copy while setup is replacing files.

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
- Idempotent NSSM configuration with read-back verification
- Supported NSSM rotation settings and localized service-state handling
- Rollback after failed updates and transient service-manager retries
- Background-service crash recovery
- Clean install, v3 upgrade, v4 replacement, uninstall, DNS recovery, and reboot validation
