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
C:\ProgramData\BeszelAgentManager\nssm\nssm.exe
```

## Logs

- Manager and broker: `C:\ProgramData\BeszelAgentManager\manager.log`
- Agent: `C:\ProgramData\BeszelAgentManager\agent_logs`
- Last helper failure: `C:\ProgramData\BeszelAgentManager\helper-last-error.txt`

Use **Extra → Create support bundle** to collect redacted diagnostics.

## Broker unavailable

1. Confirm `BeszelAgentManager Background` is running.
2. Confirm its executable path points to `C:\Program Files\BeszelAgentManager\app\helper`.
3. Re-run the current manager installer to repair the service and policy.
4. Do not manually broaden ProgramData ACLs.

## Failed manager update

The service rejects missing checksums, checksum mismatches, invalid signatures, unexpected asset names, invalid versions, oversized downloads, and reparse points. Review `manager.log`, then retry from **Manage Manager Version…**.

## DNS fallback recovery

The current mode and success streak are stored in `dns-fallback-state.json`. Restoring valid primary DNS should return to primary mode after five one-minute checks. Disabling fallback applies the primary immediately.

The guarded VM validation is available at:

```text
tests\integration\Invoke-DnsFailoverValidation.ps1
```

It backs up configuration and NSSM environment, performs the invalid-host test, and restores original state in a `finally` block.

## Reinstall and migration

Reinstalling v4 repairs application files and the broker without replacing the running NSSM binary. Upgrading from v3.1.0 preserves configuration, token ciphertext, agent state, environment, and logs.

Manager uninstall leaves the independently running agent, stable NSSM wrapper, and—when selected—historical agent logs. Use the manager’s separate **Uninstall agent** action to remove the agent.
