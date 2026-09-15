# Security model

## Trust boundaries

- The WinUI process runs as the signed-in user and is not trusted with administrator privileges.
- The background service runs as LocalSystem and accepts only explicitly allowlisted broker actions.
- The Windows account that installed the manager is the only non-SYSTEM broker client.
- Administrators and SYSTEM control installation files, policy, and privileged staging directories.

## Broker authorization

- Fixed local pipe: `BeszelAgentManager.Background.v1`
- Protocol version: `1`
- Pipe ACL: authorized installer SID and SYSTEM; network SID denied
- Server-side verification: connected client is impersonated and its SID must match policy
- Client-side verification: pipe server PID, service image path, and LocalSystem service account must match
- First server instance: `PipeOptions.FirstPipeInstance`
- Request size: bounded before JSON parsing
- Mutations: serialized through one process-wide gate
- Arguments: action-specific booleans, versions, and release tags are validated

The readable account name is used only for logging. Authorization always compares SIDs.

## Filesystem controls

`C:\ProgramData\BeszelAgentManager` contains user-editable configuration and UI logs. LocalSystem writes only below `C:\ProgramData\BeszelAgentManager.ServiceData`, which has protected ACLs granting the authorized account read and execute access but no write access. NSSM and the agent executable are protected below `C:\Program Files\Beszel-Agent`.

Before privileged recursive cleanup, the service:

1. verifies the path remains beneath the protected service-data root;
2. rejects a reparse-point root;
3. removes inherited write access;
4. walks without following reparse points;
5. rejects any nested reparse point;
6. deletes only after validation and ACL hardening.

Broker configuration is size-bounded and validated before use. Only explicit Beszel Agent variables are accepted. File-backed `TOKEN_FILE`, `KEY_FILE`, and `DATA_DIR` settings and arbitrary process-environment names are rejected for the LocalSystem agent.

## Update verification

Manager updates:

- accept only a validated version tag;
- retrieve metadata from the hardcoded manager repository;
- require the exact Bundled or Lite installer name selected from the installed edition and the exact checksum asset name;
- require HTTPS GitHub asset URLs;
- enforce download-size limits;
- verify the SHA-256 entry for `BeszelAgentManagerSetup.exe` or `BeszelAgentManagerSetup-Lite.exe` as appropriate;
- require a valid Authenticode signature for broker installation;
- stage under a service-controlled directory;
- launch only the fixed staged installer with fixed silent arguments;
- refuse tags older than the installed manager.

Current public installers are unsigned. The WinUI update flow therefore opens the selected official GitHub release page and requires normal administrator approval for manual installation instead of asking the service to run the installer as SYSTEM.

Agent updates accept only the expected Windows x64 asset and versioned checksum file from the hardcoded Beszel repository, enforce HTTPS GitHub URLs and size limits, verify the archive's SHA-256 entry, and extract only a verified archive into a restricted temporary directory. Missing checksum assets, missing archive entries, mismatched hashes, and broker-requested downgrades are rejected before installation.

## Secrets

- The optional GitHub token is encrypted with DPAPI `CurrentUser` scope.
- Support bundles replace key, token, and encrypted-token fields with redacted values.
- Logs do not intentionally write token values.
- Credential-bearing `ALL_PROXY` values are redacted from manager logs and support bundles.
- Broker policy stores a SID, not credentials.

## UAC boundary

Normal privileged actions use the installed broker. UAC remains for:

- initial installation;
- manager upgrade/uninstall when invoked interactively;
- the interactive NSSM **Edit service…** command.

## 4.0.10 audit status

The final source audit covered broker authentication, command and URL allowlists, privileged process resolution, download verification and limits, archive staging, reparse-point handling, ProgramData ACLs, secret redaction, uninstall cleanup, and dependency advisories.

Remediated findings include privileged writes beneath user-controlled ProgramData, unvalidated LocalSystem environment variables, pipe squatting and overbroad client impersonation, unsigned SYSTEM manager updates, and broker-requested downgrades. Debug and Release builds, core tests, NuGet advisory checks, and the private CI/VM installer matrix remain release gates.
