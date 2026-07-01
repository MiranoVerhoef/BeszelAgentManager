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
- Request size: bounded before JSON parsing
- Mutations: serialized through one process-wide gate
- Arguments: action-specific booleans, versions, and release tags are validated

The readable account name is used only for logging. Authorization always compares SIDs.

## Filesystem controls

`C:\ProgramData\BeszelAgentManager` is restricted to SYSTEM, Administrators, and the authorized installer account. The manager-update directory, temporary agent-download directory, and stable NSSM service-wrapper directory are further restricted to SYSTEM and Administrators.

Before privileged recursive cleanup, the service:

1. verifies the path remains beneath manager ProgramData;
2. rejects a reparse-point root;
3. removes inherited write access;
4. walks without following reparse points;
5. rejects any nested reparse point;
6. deletes only after validation and ACL hardening.

## Update verification

Manager updates:

- accept only a validated version tag;
- retrieve metadata from the hardcoded manager repository;
- require exact installer and checksum asset names;
- require HTTPS GitHub asset URLs;
- enforce download-size limits;
- verify the SHA-256 entry for `BeszelAgentManagerSetup.exe`;
- reject invalid Authenticode signatures and require a valid signature when present;
- stage under a service-controlled directory;
- launch only the fixed staged installer with fixed silent arguments.

Agent updates accept only the expected Windows x64 asset from the hardcoded Beszel repository, enforce HTTPS GitHub URLs and a size limit, and extract into a restricted temporary directory.

## Secrets

- The optional GitHub token is encrypted with DPAPI `CurrentUser` scope.
- Support bundles replace key, token, and encrypted-token fields with redacted values.
- Logs do not intentionally write token values.
- Broker policy stores a SID, not credentials.

## UAC boundary

Normal privileged actions use the installed broker. UAC remains for:

- initial installation;
- manager upgrade/uninstall when invoked interactively;
- the interactive NSSM **Edit service…** command.

## Residual risks and release requirements

- Unsigned releases rely on the repository-hosted SHA-256 manifest and GitHub account security. Signing manager installers is recommended.
- Administrators and SYSTEM remain trusted and can replace installed files or policy.
- The CI pull-request installer artifact must pass the same VM matrix before stable release.
- `4.0.0` remains an RC until the final matrix passes.

## 4.0.0 RC audit status

The final source audit covered broker authentication, command and URL allowlists, privileged process resolution, download verification and limits, archive staging, reparse-point handling, ProgramData ACLs, secret redaction, uninstall cleanup, and dependency advisories.

Remediated findings include broad ProgramData write access, unsafe privileged recursive deletion, PATH-based system-tool resolution, unbounded broker/download input, unrestricted update asset URLs, and invalid custom environment names or multiline values. Debug and Release builds complete without warnings, all core tests pass, and NuGet reports no known vulnerable direct or transitive packages. Installer signing and the CI/VM artifact matrix remain release gates rather than source-code findings.
