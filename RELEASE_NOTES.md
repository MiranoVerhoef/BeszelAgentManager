# BeszelAgentManager v3.1.0

## Resilient service configuration

- Applies only NSSM settings that differ from the desired configuration.
- Reads settings back after each change and fails if verification does not match.
- Uses supported NSSM 2.24 rotation settings and handles localized Windows service-state output.
- Retries transient Windows service-manager deletion and locking errors.
- Confirms that the agent service remains running after configuration.
- Restores changed settings when updating an existing service fails.
- Removes a newly created service when its initial configuration fails.
- Adds automated regression tests for retries, idempotency, verification, and rollback.

## Hotfix

- Fixed agent installation failing while redundantly setting the NSSM service display name.

# BeszelAgentManager v3.0.0

This is the first installer-based release of BeszelAgentManager. Version 3.0.0 moves away from the old portable executable flow and gives the manager a cleaner, more reliable Windows installation experience.

## Highlights

- New Windows installer: `BeszelAgentManagerSetup.exe`
- New install layout under `C:\Program Files\BeszelAgentManager`
- Bundled NSSM installed at `C:\Program Files\BeszelAgentManager\nssm.exe`
- Cleaner migration from older standalone installs
- Optional Start Menu and Desktop shortcuts during setup
- Improved update flow for future manager releases
- Better handling for Windows Defender exclusions, permissions, services, and uninstall cleanup

## Installer and Migration

- Installs app files under `C:\Program Files\BeszelAgentManager\app`
- Keeps NSSM at the install root for simpler service paths
- Detects old root-layout installs in `C:\Program Files\BeszelAgentManager`
- Offers a checked migration option when an old layout is found
- Stops old running manager processes before cleanup
- Migrates old autostart entries to the new installed executable
- Removes old flat Start Menu shortcuts and creates the new installer-managed shortcut
- Repairs permissions on an existing `C:\Program Files\Beszel-Agent` folder during migration

## Service Improvements

- Uses the bundled NSSM by default instead of relying on Chocolatey or external downloads
- Recreates old services that pointed to an old NSSM path
- Handles stale Windows service deletion by checking the service PID and killing the stale process when needed
- Removes unsupported NSSM settings that caused service setup failures
- Cleans up service error output so messages are easier to read

## Updating

- Manager updates now use the installer-based flow
- Release assets are expected to be named `BeszelAgentManagerSetup.exe`
- The app validates downloaded installers before running them
- Prerelease update checks can be enabled from the manager settings

## Fixes and Polish

- Fixed first-run permissions on clean Windows installs
- Fixed normal-user startup after setup
- Fixed GUI and installer icon handling
- Improved uninstall cleanup for Program Files and ProgramData
- Reduced noisy scheduled-task logs
- Preserved edited settings before admin relaunch
- Improved support bundle diagnostics

## Notes

If you are upgrading from an older standalone version, run the installer normally. The setup will detect the old layout and offer the migration cleanup option automatically.
