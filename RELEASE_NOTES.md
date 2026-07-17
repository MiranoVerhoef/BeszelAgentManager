## Summary

- Bumped version to 4.0.6.
- Replaced routine `sc.exe` polling with a secured background-service status request.
- Cached agent service metadata and version information.
- Reduced normal visible status refreshes to every 10 seconds.
- Reduced hidden tray status refreshes to every 60 seconds.
- Kept immediate status refreshes after start, stop, restart, install, update, uninstall, and configuration actions.

## Impact

Normal manager usage no longer launches `sc.exe` repeatedly. Service mutations, installation, repair, diagnostics, and explicit Extra-page checks may still use Windows service-management commands when required.
