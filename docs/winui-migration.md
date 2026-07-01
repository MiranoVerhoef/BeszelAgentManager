# Version 4 migration status

BeszelAgentManager 4.0.0 replaces the Python/Tk/Nuitka application with .NET 10, WinUI 3, a shared Core library, and a native LocalSystem background service.

## Completed

- WinUI feature parity and tray behavior
- secured no-UAC broker for routine privileged actions
- background-service scheduling
- DNS failover persistence and automated tests
- manager version selection, rollback, force reinstall, and notifications
- checksum-verified silent manager installation
- optional Defender exclusion and third-party antivirus instructions
- v3.1.0 configuration, token, logs, service, NSSM, and autostart migration
- clean install, v4 replacement, uninstall, crash recovery, and reboot validation
- Python and early WinUI scaffold removal
- generated distribution removal
- architecture, security, and recovery documentation

## Remaining release gates

1. Complete final code optimization and security review.
2. Run clean Debug and Release builds, unit tests, publish, and installer compilation.
3. Build and test the pull-request installer artifact from GitHub Actions.
4. Confirm the final feature-parity checklist.
5. Keep `RELEASE_CHANNEL=rcN` until the full matrix passes.
