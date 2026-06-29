# BeszelAgentManager 4.0.0 WinUI Migration

BeszelAgentManager 4.0.0 is the migration path from the Python/Tk/Nuitka desktop app to a native WinUI 3 / Windows App SDK app.

## Goals

- Keep the main UI running without elevation.
- Elevate only a focused helper process for service, firewall, Defender, scheduled task, and Program Files changes.
- Move release/update checks into native C# code.
- Preserve the installer-based update flow introduced in 3.0.0.
- Keep GitHub releases using `BeszelAgentManagerSetup.exe` as the public asset.
- Remove Python packaging once the WinUI app reaches feature parity.

## Migration Slices

1. Create the WinUI shell with the target navigation model.
2. Implement read-only manager update checks.
3. Add read-only agent/service/log status.
4. Extract current Python behavior behind stable backend boundaries.
5. Add the elevated helper executable.
6. Move config load/save into C#.
7. Move service, NSSM, Defender, and scheduled task operations into the helper.
8. Update the Inno Setup installer for the WinUI layout.
9. Remove Nuitka/Python build output when all flows are ported.

## Update Behavior

The WinUI update path must never offer to reinstall the same manager version from the normal download flow. If the latest GitHub release version is equal to or older than the installed version, the UI shows `Up to date` and does not start the installer.

Force reinstall can be added later as an explicit advanced command.

## Release Channel

The `4.0.0` branch starts with `RELEASE_CHANNEL=rc1` to avoid accidentally publishing a stable 4.0.0 release while the WinUI migration is incomplete.
