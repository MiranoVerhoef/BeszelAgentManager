# Architecture

## Components

### WinUI application

`BeszelAgentManager.WinUI` is the unelevated interactive application. It owns navigation, configuration editing, status display, release selection, tray behavior, DPAPI token storage, log viewing, and support-bundle creation.

Hidden startup pre-renders the real manager window off-screen and hides it after its first composed frame. Opening from the tray therefore shows the completed UI without a splash or intermediate cover window.

### Background service and broker

`BeszelAgentManager.Helper.exe --background-service` is installed as the automatic LocalSystem service `BeszelAgentManager Background`.

It owns:

- the version 1 named-pipe broker;
- all privileged agent, service, firewall, Defender, and installer operations;
- DNS fallback monitoring;
- automatic agent updates;
- periodic agent restarts;
- daily agent-log rotation.

Mutating operations share one semaphore, preventing tray, UI, and schedule actions from changing service state concurrently. Service start, stop, and restart operations return success only after the requested final state is verified.

### Core library

`BeszelAgentManager.Core` contains the shared broker contract and DNS failover coordinator. DNS, agent configuration, time, and persistence are injected boundaries so failover decisions can be tested without changing a real service.

### Installer

Inno Setup installs the self-contained WinUI application and helper, places NSSM in a stable ProgramData directory, configures the background service, and preserves v3.1.0 data during upgrade.

## State

| File | Purpose |
| --- | --- |
| `config.json` | User configuration and typed v3-compatible settings |
| `broker-policy.json` | Protocol version and authorized installer-user SID |
| `background-runtime-state.json` | Schedule last-run and next-due timestamps |
| `dns-fallback-state.json` | Active DNS mode and recovery streak |
| `manager.log` | UI and background-service operational log |

All paths are under `C:\ProgramData\BeszelAgentManager` unless otherwise stated.

## Broker protocol

Requests contain:

- protocol version;
- request ID;
- allowlisted action name;
- action-specific string arguments.

Responses contain:

- protocol version;
- matching request ID;
- completion state;
- success flag;
- stable numeric error code;
- user-safe message.

The endpoint is local-only. Pipe ACLs permit SYSTEM and the authorized installer account, deny network clients, and the service impersonates each connection to verify the caller SID again.

## DNS fallback states

1. Primary mode resolves the primary Hub hostname every minute.
2. A failed lookup applies the configured fallback once.
3. Continued failures retain fallback without repeatedly reconfiguring the agent.
4. Successful primary lookups increment a recovery streak.
5. Five consecutive successes restore the primary URL.
6. Disabling fallback restores the primary immediately.

State is persisted so service restart does not lose the active mode or recovery streak.
