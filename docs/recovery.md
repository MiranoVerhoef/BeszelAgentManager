# Recovery and diagnostics

## Service recovery

`BeszelAgentManager Background` is automatic and configured for three restart attempts after unexpected termination. A normal installer or uninstall stop does not trigger crash recovery.

Check status:

```powershell
Get-Service 'BeszelAgentManager Background', 'Beszel Agent'
```

Inspect service paths:

```powershell
sc.exe qc "BeszelAgentManager Background"
sc.exe qc "Beszel Agent"
```

The Beszel Agent service should use the quoted stable wrapper:

```text
C:\Program Files\Beszel-Agent\nssm.exe
```

## Logs

- Manager UI: `C:\ProgramData\BeszelAgentManager\manager.log`
- Background service: `C:\ProgramData\BeszelAgentManager.ServiceData\background-service.log`
- Agent: `C:\ProgramData\BeszelAgentManager.ServiceData\agent_logs`
- Last helper failure: `C:\ProgramData\BeszelAgentManager.ServiceData\helper-last-error.txt`

Use **Extra → Create support bundle** to collect redacted diagnostics.

## Broker unavailable

1. Confirm `BeszelAgentManager Background` is running.
2. Confirm its executable path points to `C:\Program Files\BeszelAgentManager\app\helper`.
3. Re-run the current manager installer to repair the service and policy.
4. Do not manually broaden ProgramData ACLs.

## Failed manager update

Use **Manage Manager Version…** to open the official release. Download the installer matching the installed edition, verify it against `SHA256SUMS.txt`, and approve the installer through Windows. The service refuses unsigned silent installation and version downgrades.

## Failed agent update

The service requires the official release's `beszel_<version>_checksums.txt` asset and an exact SHA-256 match for `beszel-agent_windows_amd64.zip`. A missing or mismatched checksum leaves the installed agent binary untouched. Review `manager.log`, confirm the selected official Beszel release contains both assets, then retry.

## DNS fallback recovery

The current mode and success streak are stored in the protected service-data `dns-fallback-state.json`. Restoring valid primary DNS should return to primary mode after five one-minute checks. Disabling fallback applies the primary immediately.

The guarded VM validation is available at:

```text
tests\integration\Invoke-DnsFailoverValidation.ps1
```

It backs up configuration and NSSM environment, performs the invalid-host test, and restores original state in a `finally` block.

## Reinstall and migration

Reinstalling v4 repairs application files and the broker while preserving an existing protected NSSM binary. Upgrading from v3.1.0 preserves configuration, token ciphertext, safe allowlisted agent settings, agent state, and logs. Legacy logs remain readable from their old directory without being copied by LocalSystem.

Manager uninstall leaves the independently running agent, stable NSSM wrapper, and—when selected—historical agent logs. Use the manager’s separate **Uninstall agent** action to remove the agent.
