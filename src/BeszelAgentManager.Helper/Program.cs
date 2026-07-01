using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Text;
using BeszelAgentManager.Core;
using Microsoft.Win32;

const string serviceName = "Beszel Agent";
const string legacyServiceName = "BeszelAgentManager";
const string agentRepo = "henrygd/beszel";
const string managerRepo = "MiranoVerhoef/BeszelAgentManager";
const string agentZipName = "beszel-agent_windows_amd64.zip";
const string managerInstallerName = "BeszelAgentManagerSetup.exe";
const string managerChecksumName = "SHA256SUMS.txt";
const string firewallRuleName = "Beszel Agent";
const string updateTaskName = "BeszelAgentManagerUpdate";
const string agentLogRotateTaskName = "BeszelAgentManagerAgentLogRotate";
const string restartTaskName = "BeszelAgentManagerRestartService";
const string backgroundServiceName = "BeszelAgentManager Background";
const string brokerPipeName = "BeszelAgentManager.Background.v1";
const string brokerPolicyFileName = "broker-policy.json";
const string backgroundRuntimeStateFileName = "background-runtime-state.json";
const int brokerProtocolVersion = 1;
const string autoUpdateStampName = "last-auto-update-agent.txt";
const int ServiceAlreadyRunning = 1056;
const int ServiceNotRunning = 1062;

if (args.Length == 1 && string.Equals(args[0], "--auto-update-agent", StringComparison.OrdinalIgnoreCase))
{
    return await RunScheduledAgentUpdateAsync();
}

if (args.Length == 1 && string.Equals(args[0], "--background-service", StringComparison.OrdinalIgnoreCase))
{
    return RunBackgroundWindowsService();
}

if (args.Length == 1 && string.Equals(args[0], "--install-background-service", StringComparison.OrdinalIgnoreCase))
{
    return await InstallOrUpdateBackgroundServiceAsync();
}

if (args.Length is 1 or 2 && string.Equals(args[0], "--remove-background-service", StringComparison.OrdinalIgnoreCase))
{
    var removeAgentLogs = args.Length == 2
        && string.Equals(args[1], "--remove-agent-logs", StringComparison.OrdinalIgnoreCase);
    return await RemoveBackgroundServiceAsync(removeAgentLogs);
}

if (args.Length == 2 && string.Equals(args[0], "--apply-hub-url", StringComparison.OrdinalIgnoreCase))
{
    return await ApplyHubUrlOverrideAsync(serviceName, args[1]);
}

if (args.Length == 1 && string.Equals(args[0], "--edit-service", StringComparison.OrdinalIgnoreCase))
{
    return OpenServiceEditor(serviceName);
}

return 2;

static async Task<int> RestartServiceAsync(string name)
{
    var stopResult = await StopServiceAsync(name, waitForCompletion: false);
    if (stopResult != 0)
    {
        return stopResult;
    }

    var stopDeadline = DateTime.UtcNow.AddSeconds(15);
    while (DateTime.UtcNow < stopDeadline)
    {
        var snapshot = await QueryServiceAsync(name);
        if (!snapshot.Exists || IsStoppedState(snapshot.State))
        {
            break;
        }

        await Task.Delay(500);
    }

    var afterStop = await QueryServiceAsync(name);
    if (afterStop.Exists && !IsStoppedState(afterStop.State) && afterStop.Pid > 0)
    {
        await RunProcessAsync("taskkill.exe", ["/F", "/PID", afterStop.Pid.ToString()]);
        var killDeadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < killDeadline)
        {
            var snapshot = await QueryServiceAsync(name);
            if (!snapshot.Exists || IsStoppedState(snapshot.State) || snapshot.Pid == 0)
            {
                break;
            }

            await Task.Delay(500);
        }
    }

    if (!await WaitForServiceStateAsync(name, "STOPPED", TimeSpan.FromSeconds(8)))
    {
        return 4;
    }

    return await StartServiceAsync(name);
}

static int OpenServiceEditor(string name)
{
    var nssmPath = FindNssmPath();
    if (nssmPath is null)
    {
        return 53;
    }

    Process.Start(new ProcessStartInfo
    {
        FileName = nssmPath,
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { "edit", name },
    });
    return 0;
}

static string? FindNssmPath()
{
    var baseDirectory = AppContext.BaseDirectory;
    var candidates = new[]
    {
        Path.Combine(ProgramDataPath(), "BeszelAgentManager", "nssm", "nssm.exe"),
        Path.Combine(baseDirectory, "nssm.exe"),
        Path.Combine(Path.GetFullPath(Path.Combine(baseDirectory, "..")), "nssm.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BeszelAgentManager", "nssm.exe"),
    };

    foreach (var candidate in candidates)
    {
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return null;
}

static async Task<int> InstallOrUpdateBackgroundServiceAsync()
{
    var policyResult = EnsureBrokerPolicy();
    if (policyResult != 0)
    {
        return policyResult;
    }
    CleanupLegacyManagerArtifacts();

    var executablePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BeszelAgentManager.Helper.exe");
    var binaryPath = $"\"{executablePath}\" --background-service";
    var configure = await RunProcessAsync(
        "sc.exe",
        await ServiceExistsAsync(backgroundServiceName)
            ? ["config", backgroundServiceName, "binPath=", binaryPath, "start=", "auto", "DisplayName=", "BeszelAgentManager Background"]
            : ["create", backgroundServiceName, "binPath=", binaryPath, "start=", "auto", "DisplayName=", "BeszelAgentManager Background"]);
    if (configure.ExitCode != 0)
    {
        return 4;
    }

    await RunProcessAsync(
        "sc.exe",
        ["description", backgroundServiceName, "Provides automatic background monitoring and Hub URL failover for BeszelAgentManager."]);
    await RunProcessAsync(
        "sc.exe",
        ["failure", backgroundServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/30000"]);
    await RunProcessAsync("sc.exe", ["failureflag", backgroundServiceName, "1"]);
    await DeleteLegacyScheduledTasksAsync();

    return await RestartServiceAsync(backgroundServiceName);
}

static void CleanupLegacyManagerArtifacts()
{
    var dataDirectory = Path.Combine(ProgramDataPath(), "BeszelAgentManager");
    var legacyUpdates = Path.Combine(dataDirectory, "updates");
    if (Directory.Exists(legacyUpdates))
    {
        ResetPrivilegedWorkingDirectory(legacyUpdates);
        Directory.Delete(legacyUpdates);
    }

    TryDeleteFile(Path.Combine(dataDirectory, "update-beszel-agent.ps1"));
    TryDeleteFile(Path.Combine(dataDirectory, "update-manager.ps1"));
    TryDeleteFile(Path.Combine(dataDirectory, "acl_done.flag"));
}

static async Task<int> RemoveBackgroundServiceAsync(bool removeAgentLogs)
{
    try
    {
        await SetDefenderExclusionAsync(false);
    }
    catch
    {
    }

    await DeleteLegacyScheduledTasksAsync();
    if (await ServiceExistsAsync(backgroundServiceName))
    {
        await StopServiceAsync(backgroundServiceName);
        await RunProcessAsync("sc.exe", ["delete", backgroundServiceName]);
    }

    try
    {
        CleanupManagerDataForUninstall(removeAgentLogs);
        return 0;
    }
    catch (Exception ex)
    {
        WriteHelperLastError(ex);
        return 4;
    }
}

static void CleanupManagerDataForUninstall(bool removeAgentLogs)
{
    var dataDirectory = Path.Combine(ProgramDataPath(), "BeszelAgentManager");
    if (!Directory.Exists(dataDirectory))
    {
        return;
    }

    foreach (var name in new[] { "manager_logs", "support_bundles", "updates", "manager-update", "tmp_agent" })
    {
        DeletePrivilegedDirectory(Path.Combine(dataDirectory, name));
    }
    if (removeAgentLogs)
    {
        DeletePrivilegedDirectory(Path.Combine(dataDirectory, "agent_logs"));
    }

    foreach (var file in Directory.EnumerateFiles(dataDirectory))
    {
        File.Delete(file);
    }
}

static void DeletePrivilegedDirectory(string path)
{
    if (!Directory.Exists(path))
    {
        return;
    }

    ResetPrivilegedWorkingDirectory(path);
    Directory.Delete(path);
}

static int RunBackgroundWindowsService()
{
    ServiceBase.Run(new BackgroundWindowsService(RunBackgroundLoopAsync));
    return 0;
}

static async Task RunBackgroundLoopAsync(CancellationToken cancellationToken)
{
    var failoverStore = new JsonDnsFailoverStateStore(
        Path.Combine(ProgramDataPath(), "BeszelAgentManager", "dns-fallback-state.json"));
    var state = failoverStore.Load();
    var runtimeState = LoadBackgroundRuntimeState();
    WriteBackgroundLog("INFO", "Background service started");
    try
    {
        await Task.WhenAll(
            RunBackgroundMonitoringLoopAsync(state, failoverStore, runtimeState, cancellationToken),
            RunBrokerServerAsync(cancellationToken));
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    catch (Exception ex)
    {
        WriteBackgroundLog("ERROR", $"Background service stopped unexpectedly: {ex}");
        throw;
    }
    finally
    {
        WriteBackgroundLog("INFO", "Background service stopped");
    }
}

static async Task RunBackgroundMonitoringLoopAsync(
    DnsFailoverState state,
    IDnsFailoverStateStore failoverStore,
    BackgroundRuntimeState runtimeState,
    CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await CheckHubFailoverAsync(state, failoverStore, cancellationToken);
        await RunDueBackgroundSchedulesAsync(runtimeState, cancellationToken);

        using var cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(TimeSpan.FromMinutes(1), cycleCancellation.Token);
        var reload = BrokerRuntime.ReloadSignal.WaitAsync(cycleCancellation.Token);
        await Task.WhenAny(delay, reload);
        cycleCancellation.Cancel();
        try
        {
            await Task.WhenAll(delay, reload);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
}

static async Task RunDueBackgroundSchedulesAsync(
    BackgroundRuntimeState state,
    CancellationToken cancellationToken)
{
    using var config = await LoadConfigurationAsync();
    if (config is null)
    {
        return;
    }

    var now = DateTimeOffset.Now;
    var changed = ReconcileScheduleState(config.RootElement, state, now);

    if (state.NextAgentUpdateAt is { } updateDue && updateDue <= now)
    {
        await BrokerRuntime.MutationGate.WaitAsync(cancellationToken);
        try
        {
            WriteBackgroundLog("INFO", "Scheduled agent update check started.");
            var result = await UpdateAgentIfNewerAsync(config.RootElement);
            state.LastAgentUpdateAt = now;
            state.NextAgentUpdateAt = now.AddHours(state.AgentUpdateIntervalHours);
            state.LastOperation = result == 0 ? "agent-update:success" : $"agent-update:failed:{result}";
            WriteBackgroundLog(
                result == 0 ? "INFO" : "ERROR",
                result == 0
                    ? $"Scheduled agent update check completed. Next check: {state.NextAgentUpdateAt:O}."
                    : $"Scheduled agent update check failed with code {result}. Next check: {state.NextAgentUpdateAt:O}.");
            changed = true;
        }
        finally
        {
            BrokerRuntime.MutationGate.Release();
        }
    }

    if (state.NextPeriodicRestartAt is { } restartDue && restartDue <= now)
    {
        await BrokerRuntime.MutationGate.WaitAsync(cancellationToken);
        try
        {
            WriteBackgroundLog("INFO", "Scheduled Beszel Agent restart started.");
            var result = await RestartServiceAsync(serviceName);
            state.LastPeriodicRestartAt = now;
            state.NextPeriodicRestartAt = now.AddMinutes(state.PeriodicRestartIntervalMinutes);
            state.LastOperation = result == 0 ? "periodic-restart:success" : $"periodic-restart:failed:{result}";
            WriteBackgroundLog(
                result == 0 ? "INFO" : "ERROR",
                result == 0
                    ? $"Scheduled Beszel Agent restart completed. Next restart: {state.NextPeriodicRestartAt:O}."
                    : $"Scheduled Beszel Agent restart failed with code {result}. Next restart: {state.NextPeriodicRestartAt:O}.");
            changed = true;
        }
        finally
        {
            BrokerRuntime.MutationGate.Release();
        }
    }

    if (state.NextAgentLogRotationAt is { } rotationDue && rotationDue <= now)
    {
        await BrokerRuntime.MutationGate.WaitAsync(cancellationToken);
        try
        {
            WriteBackgroundLog("INFO", "Scheduled agent log rotation started.");
            var result = await RotateAgentLogsAsync();
            state.LastAgentLogRotationAt = now;
            state.NextAgentLogRotationAt = NextDailyAgentLogRotation(now);
            state.LastOperation = result == 0 ? "agent-log-rotation:success" : $"agent-log-rotation:failed:{result}";
            WriteBackgroundLog(
                result == 0 ? "INFO" : "ERROR",
                result == 0
                    ? $"Scheduled agent log rotation completed. Next rotation: {state.NextAgentLogRotationAt:O}."
                    : $"Scheduled agent log rotation failed with code {result}. Next rotation: {state.NextAgentLogRotationAt:O}.");
            changed = true;
        }
        finally
        {
            BrokerRuntime.MutationGate.Release();
        }
    }

    if (changed)
    {
        await SaveBackgroundRuntimeStateAsync(state);
    }
}

static bool ReconcileScheduleState(JsonElement config, BackgroundRuntimeState state, DateTimeOffset now)
{
    var changed = false;
    var updateEnabled = IsAutoUpdateEnabled(config);
    var updateInterval = GetUpdateIntervalHours(config);
    if (!updateEnabled)
    {
        if (state.NextAgentUpdateAt is not null || state.AgentUpdateIntervalHours != updateInterval)
        {
            state.NextAgentUpdateAt = null;
            state.AgentUpdateIntervalHours = updateInterval;
            changed = true;
        }
    }
    else if (state.NextAgentUpdateAt is null || state.AgentUpdateIntervalHours != updateInterval)
    {
        state.AgentUpdateIntervalHours = updateInterval;
        state.NextAgentUpdateAt = now.AddHours(updateInterval);
        changed = true;
    }

    var restartEnabled = config.TryGetProperty("auto_restart_enabled", out var restartEnabledValue)
        && restartEnabledValue.ValueKind == JsonValueKind.True;
    var restartIntervalMinutes = GetPeriodicRestartIntervalMinutes(config);
    if (!restartEnabled)
    {
        if (state.NextPeriodicRestartAt is not null || state.PeriodicRestartIntervalMinutes != restartIntervalMinutes)
        {
            state.NextPeriodicRestartAt = null;
            state.PeriodicRestartIntervalMinutes = restartIntervalMinutes;
            changed = true;
        }
    }
    else if (state.NextPeriodicRestartAt is null || state.PeriodicRestartIntervalMinutes != restartIntervalMinutes)
    {
        state.PeriodicRestartIntervalMinutes = restartIntervalMinutes;
        state.NextPeriodicRestartAt = now.AddMinutes(restartIntervalMinutes);
        changed = true;
    }

    if (state.NextAgentLogRotationAt is null)
    {
        state.NextAgentLogRotationAt = NextDailyAgentLogRotation(now);
        changed = true;
    }

    return changed;
}

static int GetPeriodicRestartIntervalMinutes(JsonElement config)
{
    var unit = config.TryGetProperty("auto_restart_interval_unit", out var unitProperty)
        && unitProperty.ValueKind == JsonValueKind.String
        && string.Equals(unitProperty.GetString(), "minutes", StringComparison.OrdinalIgnoreCase)
            ? "minutes"
            : "hours";
    if (config.TryGetProperty("auto_restart_interval_value", out var valueProperty))
    {
        var selectedValue = valueProperty.ValueKind == JsonValueKind.Number && valueProperty.TryGetInt32(out var numericValue)
            ? numericValue
            : valueProperty.ValueKind == JsonValueKind.String && int.TryParse(valueProperty.GetString(), out var textValue)
                ? textValue
                : 0;
        if (selectedValue > 0)
        {
            return unit == "minutes"
                ? Math.Clamp(selectedValue, 1, 10080)
                : Math.Clamp(selectedValue, 1, 168) * 60;
        }
    }

    if (!config.TryGetProperty("auto_restart_interval_hours", out var legacyProperty))
    {
        return 24 * 60;
    }

    var legacyHours = legacyProperty.ValueKind == JsonValueKind.Number && legacyProperty.TryGetInt32(out var numeric)
        ? numeric
        : legacyProperty.ValueKind == JsonValueKind.String && int.TryParse(legacyProperty.GetString(), out var legacyTextValue)
            ? legacyTextValue
            : 24;
    return Math.Clamp(legacyHours, 1, 168) * 60;
}

static DateTimeOffset NextDailyAgentLogRotation(DateTimeOffset now)
{
    var localNow = now.LocalDateTime;
    var next = localNow.Date.AddMinutes(5);
    if (next <= localNow)
    {
        next = next.AddDays(1);
    }

    return new DateTimeOffset(next, TimeZoneInfo.Local.GetUtcOffset(next));
}

static async Task RunBrokerServerAsync(CancellationToken cancellationToken)
{
    var policy = LoadBrokerPolicy();
    if (policy is null
        || policy.ProtocolVersion != brokerProtocolVersion
        || !IsValidSid(policy.AuthorizedSid))
    {
        throw new InvalidOperationException("The background broker policy is missing or invalid.");
    }

    var pipeSecurity = CreateBrokerPipeSecurity(policy.AuthorizedSid);
    WriteBackgroundLog(
        "INFO",
        $"Privileged broker listening for authorized account {DisplayAccountName(policy.AuthorizedSid)}.");

    while (!cancellationToken.IsCancellationRequested)
    {
        var pipe = NamedPipeServerStreamAcl.Create(
            brokerPipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            pipeSecurity);
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken);
            _ = HandleBrokerConnectionAsync(pipe, policy.AuthorizedSid, cancellationToken);
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }
}

static string DisplayAccountName(string sid)
{
    try
    {
        return new SecurityIdentifier(sid)
            .Translate(typeof(NTAccount))
            .Value;
    }
    catch
    {
        return "the configured installer account";
    }
}

static async Task HandleBrokerConnectionAsync(
    NamedPipeServerStream pipe,
    string authorizedSid,
    CancellationToken cancellationToken)
{
    await using (pipe)
    {
        BrokerRequest? request = null;
        try
        {
            var callerSid = GetConnectedClientSid(pipe);
            if (!string.Equals(callerSid, authorizedSid, StringComparison.OrdinalIgnoreCase))
            {
                await WriteBrokerResponseAsync(pipe, BrokerResponse.Failed(string.Empty, 5, "Access denied."));
                return;
            }

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            using var reader = new StreamReader(pipe, utf8, leaveOpen: true);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var payload = await ReadBoundedLineAsync(reader, 16 * 1024, requestTimeout.Token);
            if (string.IsNullOrWhiteSpace(payload))
            {
                await WriteBrokerResponseAsync(pipe, BrokerResponse.Failed(string.Empty, 2, "Invalid broker request."));
                return;
            }

            request = JsonSerializer.Deserialize<BrokerRequest>(payload);
            if (request is null
                || request.ProtocolVersion != brokerProtocolVersion
                || string.IsNullOrWhiteSpace(request.RequestId)
                || request.RequestId.Length > 80)
            {
                await WriteBrokerResponseAsync(pipe, BrokerResponse.Failed(request?.RequestId ?? string.Empty, 2, "Unsupported broker request."));
                return;
            }

            if (request.Action == "agent.version")
            {
                var installedAgentPath = AgentPath();
                if (!File.Exists(installedAgentPath))
                {
                    await WriteBrokerResponseAsync(
                        pipe,
                        BrokerResponse.Failed(request.RequestId, 3, "The Beszel Agent executable was not found."));
                    return;
                }

                var installedVersion = await GetInstalledAgentVersionAsync(installedAgentPath);
                var versionMatch = Regex.Match(installedVersion, @"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$");
                if (!versionMatch.Success)
                {
                    await WriteBrokerResponseAsync(
                        pipe,
                        BrokerResponse.Failed(request.RequestId, 4, "The Beszel Agent version could not be detected."));
                    return;
                }

                await WriteBrokerResponseAsync(
                    pipe,
                    BrokerResponse.Completed(request.RequestId, versionMatch.Value));
                return;
            }

            await BrokerRuntime.MutationGate.WaitAsync(cancellationToken);
            try
            {
                var exitCode = await ExecuteBrokerActionAsync(request);
                var response = exitCode == 0
                    ? BrokerResponse.Completed(request.RequestId)
                    : BrokerResponse.Failed(request.RequestId, exitCode, "The privileged action failed.");
                await WriteBrokerResponseAsync(pipe, response);
            }
            finally
            {
                BrokerRuntime.MutationGate.Release();
            }
        }
        catch (Exception ex)
        {
            WriteBackgroundLog("ERROR", $"Broker request failed: {ex}");
            if (pipe.IsConnected)
            {
                await WriteBrokerResponseAsync(
                    pipe,
                    BrokerResponse.Failed(request?.RequestId ?? string.Empty, 1, "The privileged broker failed."));
            }
        }
    }
}

static async Task<string?> ReadBoundedLineAsync(
    StreamReader reader,
    int maximumCharacters,
    CancellationToken cancellationToken)
{
    var builder = new StringBuilder(Math.Min(maximumCharacters, 1024));
    var character = new char[1];
    while (builder.Length <= maximumCharacters)
    {
        var read = await reader.ReadAsync(character.AsMemory(), cancellationToken);
        if (read == 0)
        {
            return builder.Length == 0 ? null : builder.ToString();
        }

        if (character[0] == '\n')
        {
            return builder.ToString().TrimEnd('\r');
        }

        builder.Append(character[0]);
    }

    return null;
}

static async Task<int> ExecuteBrokerActionAsync(BrokerRequest request)
{
    var arguments = request.Arguments ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    return request.Action switch
    {
        "service.start" => await StartServiceAsync(serviceName),
        "service.stop" => await StopServiceAsync(serviceName),
        "service.restart" => await RestartServiceAsync(serviceName),
        "config.apply" => await ApplyConfigurationAndReloadAsync(),
        "schedules.apply" => await ApplyManagerTasksAsync(),
        "agent.install" => await InstallOrUpdateAgentAsync(),
        "agent.update" => await UpdateAgentAsync(null),
        "agent.installVersion" when TryReadVersion(arguments, out var version) => await UpdateAgentAsync(version),
        "agent.uninstall" when TryReadBoolean(arguments, "keepLogs", out var keepLogs) => await UninstallAgentAsync(!keepLogs),
        "agent.logs.rotate" => await RotateAgentLogsAsync(),
        "agent.fingerprint.reset" => await ResetAgentFingerprintAsync(),
        "defender.set" when TryReadBoolean(arguments, "enabled", out var enabled) => await SetDefenderExclusionAsync(enabled),
        "manager.installVersion" when TryReadManagerTag(arguments, out var tag) => await InstallManagerVersionAsync(tag),
        _ => 2,
    };
}

static async Task WriteBrokerResponseAsync(NamedPipeServerStream pipe, BrokerResponse response)
{
    await using var writer = new StreamWriter(
        pipe,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        leaveOpen: true)
    { AutoFlush = true };
    await writer.WriteLineAsync(JsonSerializer.Serialize(response));
}

static string? GetConnectedClientSid(NamedPipeServerStream pipe)
{
    string? sid = null;
    pipe.RunAsClient(() =>
    {
        using var identity = WindowsIdentity.GetCurrent(true);
        sid = identity?.User?.Value;
    });
    return sid;
}

static PipeSecurity CreateBrokerPipeSecurity(string authorizedSid)
{
    var security = new PipeSecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    security.AddAccessRule(new PipeAccessRule(
        new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
        PipeAccessRights.FullControl,
        AccessControlType.Deny));
    security.AddAccessRule(new PipeAccessRule(
        new SecurityIdentifier(authorizedSid),
        PipeAccessRights.ReadWrite,
        AccessControlType.Allow));
    security.AddAccessRule(new PipeAccessRule(
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        PipeAccessRights.FullControl,
        AccessControlType.Allow));
    return security;
}

static async Task CheckHubFailoverAsync(
    DnsFailoverState state,
    IDnsFailoverStateStore stateStore,
    CancellationToken cancellationToken)
{
    using var config = await LoadConfigurationAsync();
    if (config is null)
    {
        return;
    }

    var root = config.RootElement;
    var primary = ReadConfigString(root, "hub_url");
    var fallback = ReadConfigString(root, "hub_url_ip_fallback");
    var enabled = root.TryGetProperty("hub_url_ip_fallback_enabled", out var enabledValue)
        && enabledValue.ValueKind == JsonValueKind.True;
    var coordinator = new DnsFailoverCoordinator(
        new DelegateDnsResolver(CanResolveHostAsync),
        new DelegateAgentHubUrlConfiguration(ApplyBackgroundHubUrlAsync),
        stateStore,
        SystemFailoverClock.Instance);
    var events = await coordinator.CheckAsync(
        new(enabled, primary, fallback),
        state,
        cancellationToken);

    foreach (var failoverEvent in events)
    {
        switch (failoverEvent.Kind)
        {
            case DnsFailoverEventKind.FallbackActivated:
                WriteBackgroundLog("WARN", $"Hub DNS resolution failed for {failoverEvent.Host}. Beszel Agent HUB_URL is using fallback.");
                break;
            case DnsFailoverEventKind.RecoveryProgress:
                WriteBackgroundLog("INFO", $"Hub DNS resolution recovered for {failoverEvent.Host} ({failoverEvent.SuccessStreak}/5).");
                break;
            case DnsFailoverEventKind.PrimaryRestored:
                WriteBackgroundLog("INFO", "Hub DNS is stable again. Beszel Agent HUB_URL is using primary.");
                break;
            case DnsFailoverEventKind.DisabledPrimaryRestored:
                WriteBackgroundLog("INFO", "Hub URL IP fallback disabled. Beszel Agent HUB_URL is using primary.");
                break;
        }
    }
}

static async Task<bool> ApplyBackgroundHubUrlAsync(string hubUrl, string destination)
{
    var encodedUrl = Convert.ToBase64String(Encoding.UTF8.GetBytes(hubUrl));
    await BrokerRuntime.MutationGate.WaitAsync();
    int exitCode;
    try
    {
        exitCode = await ApplyHubUrlOverrideAsync(serviceName, encodedUrl);
    }
    finally
    {
        BrokerRuntime.MutationGate.Release();
    }
    if (exitCode != 0)
    {
        WriteBackgroundLog("ERROR", $"Hub URL switch to {destination} failed with helper exit code {exitCode}.");
        return false;
    }

    WriteBackgroundLog("INFO", $"Beszel Agent HUB_URL switched to {destination}.");
    return true;
}

static async Task<bool> CanResolveHostAsync(string host, CancellationToken cancellationToken)
{
    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        return (await System.Net.Dns.GetHostAddressesAsync(host, timeout.Token)).Length > 0;
    }
    catch
    {
        return false;
    }
}

static BackgroundRuntimeState LoadBackgroundRuntimeState()
{
    try
    {
        var path = Path.Combine(ProgramDataPath(), "BeszelAgentManager", backgroundRuntimeStateFileName);
        if (File.Exists(path))
        {
            return JsonSerializer.Deserialize<BackgroundRuntimeState>(File.ReadAllText(path))
                ?? new BackgroundRuntimeState();
        }
    }
    catch (Exception ex)
    {
        WriteBackgroundLog("WARN", $"Could not load background runtime state: {ex.Message}");
    }

    return new BackgroundRuntimeState();
}

static async Task SaveBackgroundRuntimeStateAsync(BackgroundRuntimeState state)
{
    var directory = Path.Combine(ProgramDataPath(), "BeszelAgentManager");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, backgroundRuntimeStateFileName);
    var temporaryPath = $"{path}.{Environment.ProcessId}.tmp";
    await File.WriteAllTextAsync(
        temporaryPath,
        JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    File.Move(temporaryPath, path, overwrite: true);
}

static void WriteBackgroundLog(string level, string message)
{
    try
    {
        var directory = Path.Combine(ProgramDataPath(), "BeszelAgentManager");
        Directory.CreateDirectory(directory);
        var line = $"{DateTime.Now:yyyy/MM/dd HH:mm:ss} {level} {message}{Environment.NewLine}";
        var path = Path.Combine(directory, "manager.log");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream);
                writer.Write(line);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
        }
    }
    catch
    {
    }
}

static async Task<int> StartServiceAsync(string name)
{
    var result = await RunScAsync("start", name);
    if (result == 0 || result == ServiceAlreadyRunning)
    {
        return await WaitForServiceStateAsync(name, "RUNNING", TimeSpan.FromSeconds(30)) ? 0 : 4;
    }

    return result;
}

static async Task<int> StopServiceAsync(string name, bool waitForCompletion = true)
{
    var snapshot = await QueryServiceAsync(name);
    if (snapshot.Exists && IsStoppedState(snapshot.State))
    {
        return 0;
    }

    var result = await RunScAsync("stop", name);
    if (result == 0 || result == ServiceNotRunning)
    {
        return !waitForCompletion || await WaitForServiceStateAsync(name, "STOPPED", TimeSpan.FromSeconds(30)) ? 0 : 4;
    }

    return result;
}

static async Task<bool> WaitForServiceStateAsync(string name, string expectedState, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        var snapshot = await QueryServiceAsync(name);
        if (snapshot.Exists
            && string.Equals(snapshot.State, expectedState, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        await Task.Delay(500);
    }

    var finalSnapshot = await QueryServiceAsync(name);
    return finalSnapshot.Exists
        && string.Equals(finalSnapshot.State, expectedState, StringComparison.OrdinalIgnoreCase);
}

static async Task<int> ApplyConfigurationAsync(string serviceName)
{
    var config = await LoadConfigurationAsync();
    if (config is null || !File.Exists(AgentPath()))
    {
        return 3;
    }

    if (!await ServiceExistsAsync(backgroundServiceName))
    {
        var backgroundResult = await InstallOrUpdateBackgroundServiceAsync();
        if (backgroundResult != 0)
        {
            return backgroundResult;
        }
    }

    var hubUrlOverride = await ResolveHubUrlOverrideAsync(config.RootElement);
    var result = await ConfigureServiceAsync(serviceName, AgentPath(), config.RootElement, restart: true, hubUrlOverride);
    if (result == 0 && !string.IsNullOrWhiteSpace(hubUrlOverride))
    {
        await WriteDnsFallbackStateAsync(config.RootElement, active: true, successStreak: 0);
    }

    return result;
}

static async Task<string?> ResolveHubUrlOverrideAsync(JsonElement config)
{
    if (!config.TryGetProperty("hub_url_ip_fallback_enabled", out var enabled)
        || enabled.ValueKind != JsonValueKind.True)
    {
        return null;
    }

    var primary = ReadConfigString(config, "hub_url");
    var fallback = ReadConfigString(config, "hub_url_ip_fallback");
    var host = ExtractHost(primary);
    if (host.Length == 0 || fallback.Length == 0)
    {
        return null;
    }

    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return (await System.Net.Dns.GetHostAddressesAsync(host, timeout.Token)).Length > 0 ? null : fallback;
    }
    catch
    {
        return fallback;
    }
}

static async Task WriteDnsFallbackStateAsync(JsonElement config, bool active, int successStreak)
{
    try
    {
        var directory = Path.Combine(ProgramDataPath(), "BeszelAgentManager");
        Directory.CreateDirectory(directory);
        var state = new
        {
            timestamp = DateTime.Now.ToString("s"),
            owner = "background-service",
            enabled = config.TryGetProperty("hub_url_ip_fallback_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True,
            primary = ReadConfigString(config, "hub_url"),
            fallback = ReadConfigString(config, "hub_url_ip_fallback"),
            active,
            success_streak = successStreak,
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, "dns-fallback-state.json"),
            JsonSerializer.Serialize(state));
    }
    catch
    {
    }
}

static string ReadConfigString(JsonElement config, string name)
{
    return config.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()?.Trim() ?? string.Empty
        : string.Empty;
}

static string ExtractHost(string value)
{
    var candidate = value.Contains("://", StringComparison.Ordinal) ? value : $"https://{value}";
    return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ? uri.Host.Trim() : string.Empty;
}

static async Task<int> ApplyHubUrlOverrideAsync(string serviceName, string encodedUrl)
{
    var config = await LoadConfigurationAsync();
    if (config is null || !File.Exists(AgentPath()))
    {
        return 3;
    }

    try
    {
        var hubUrl = Encoding.UTF8.GetString(Convert.FromBase64String(encodedUrl)).Trim();
        if (string.IsNullOrWhiteSpace(hubUrl))
        {
            return 2;
        }

        return await ConfigureServiceAsync(serviceName, AgentPath(), config.RootElement, restart: true, hubUrlOverride: hubUrl);
    }
    catch (FormatException)
    {
        return 2;
    }
}

static async Task<JsonDocument?> LoadConfigurationAsync()
{
    var configPath = Path.Combine(ProgramDataPath(), "BeszelAgentManager", "config.json");
    if (!File.Exists(configPath))
    {
        return null;
    }

    await using var stream = new FileStream(
        configPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 16 * 1024,
        useAsync: true);
    return await JsonDocument.ParseAsync(stream);
}

static string ProgramDataPath()
{
    return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
}

static string BrokerPolicyPath()
{
    return Path.Combine(ProgramDataPath(), "BeszelAgentManager", brokerPolicyFileName);
}

static int EnsureBrokerPolicy()
{
    try
    {
        var path = BrokerPolicyPath();
        var dataDirectory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(dataDirectory)
            && File.GetAttributes(dataDirectory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Manager data directory cannot be a reparse point.");
        }

        var existing = LoadBrokerPolicy();
        var currentSid = WindowsIdentity.GetCurrent().User?.Value;
        var authorizedSid = existing is not null && IsValidAccountSid(existing.AuthorizedSid)
            ? existing.AuthorizedSid
            : currentSid;
        if (!IsValidAccountSid(authorizedSid))
        {
            return 5;
        }

        Directory.CreateDirectory(dataDirectory);
        ApplyManagerDataSecurity(dataDirectory, authorizedSid!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new BrokerPolicy { ProtocolVersion = brokerProtocolVersion, AuthorizedSid = authorizedSid! },
                new JsonSerializerOptions { WriteIndented = true }));

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(authorizedSid!),
            FileSystemRights.Read,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        return 0;
    }
    catch (Exception ex)
    {
        WriteBackgroundLog("ERROR", $"Could not create broker policy: {ex}");
        return 5;
    }
}

static void ApplyManagerDataSecurity(string dataDirectory, string authorizedSid)
{
    var user = new SecurityIdentifier(authorizedSid);
    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

    SecureTree(dataDirectory);
    foreach (var restrictedName in new[] { "manager-update", "tmp_agent", "nssm" })
    {
        var restrictedPath = Path.Combine(dataDirectory, restrictedName);
        if (Directory.Exists(restrictedPath))
        {
            SecurePrivilegedWorkingDirectory(restrictedPath);
        }
    }

    void ApplyDirectory(string directory)
    {
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var directorySecurity = new DirectorySecurity();
        directorySecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        directorySecurity.SetOwner(administrators);
        directorySecurity.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        directorySecurity.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        directorySecurity.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(directorySecurity);
    }

    void SecureTree(string directory)
    {
        ApplyDirectory(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"Refusing to secure reparse point in manager data: {entry}");
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                SecureTree(entry);
                continue;
            }

            var fileSecurity = new FileSecurity();
            fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            fileSecurity.SetOwner(administrators);
            fileSecurity.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            fileSecurity.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, AccessControlType.Allow));
            fileSecurity.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify, AccessControlType.Allow));
            new FileInfo(entry).SetAccessControl(fileSecurity);
        }
    }
}

static BrokerPolicy? LoadBrokerPolicy()
{
    try
    {
        var path = BrokerPolicyPath();
        return File.Exists(path)
            ? JsonSerializer.Deserialize<BrokerPolicy>(File.ReadAllText(path))
            : null;
    }
    catch
    {
        return null;
    }
}

static bool IsValidSid(string? sid)
{
    if (string.IsNullOrWhiteSpace(sid))
    {
        return false;
    }

    try
    {
        _ = new SecurityIdentifier(sid);
        return true;
    }
    catch
    {
        return false;
    }
}

static bool IsValidAccountSid(string? sid)
{
    if (!IsValidSid(sid))
    {
        return false;
    }

    var identifier = new SecurityIdentifier(sid!);
    return identifier.IsAccountSid()
        && !identifier.IsWellKnown(WellKnownSidType.LocalSystemSid)
        && !identifier.IsWellKnown(WellKnownSidType.LocalServiceSid)
        && !identifier.IsWellKnown(WellKnownSidType.NetworkServiceSid);
}

static bool TryReadVersion(Dictionary<string, string> arguments, out string version)
{
    version = arguments.GetValueOrDefault("version")?.Trim() ?? string.Empty;
    return Regex.IsMatch(version, @"^v?\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);
}

static bool TryReadManagerTag(Dictionary<string, string> arguments, out string tag)
{
    tag = arguments.GetValueOrDefault("tag")?.Trim() ?? string.Empty;
    return Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+(?:-rc\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

static bool TryReadBoolean(Dictionary<string, string> arguments, string key, out bool value)
{
    return bool.TryParse(arguments.GetValueOrDefault(key), out value);
}

static async Task<int> SetDefenderExclusionAsync(bool enabled)
{
    var managerDirectoryInfo = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory));
    if (string.Equals(managerDirectoryInfo.Name, "helper", StringComparison.OrdinalIgnoreCase))
    {
        managerDirectoryInfo = managerDirectoryInfo.Parent ?? managerDirectoryInfo;
    }
    if (string.Equals(managerDirectoryInfo.Name, "app", StringComparison.OrdinalIgnoreCase))
    {
        managerDirectoryInfo = managerDirectoryInfo.Parent ?? managerDirectoryInfo;
    }

    var managerDirectory = managerDirectoryInfo.FullName.TrimEnd(Path.DirectorySeparatorChar);
    var escapedPath = managerDirectory.Replace("'", "''", StringComparison.Ordinal);
    var command = enabled
        ? $"Add-MpPreference -ExclusionPath '{escapedPath}'"
        : $"Remove-MpPreference -ExclusionPath '{escapedPath}' -ErrorAction SilentlyContinue";
    var result = await RunProcessAsync(
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command]);
    return result.ExitCode == 0 ? 0 : 4;
}

static void TryDeleteFile(string path)
{
    try
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
    catch
    {
    }
}

static string AgentPath()
{
    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Beszel-Agent", "beszel-agent.exe");
}

static async Task<int> ResetAgentFingerprintAsync()
{
    var config = await LoadConfigurationAsync();
    var agentPath = AgentPath();
    if (config is null || !File.Exists(agentPath))
    {
        return 3;
    }

    await StopServiceAsync(serviceName);
    var result = await RunAgentProcessAsync(agentPath, ["fingerprint", "reset"], config.RootElement);
    await StartServiceAsync(serviceName);
    return result;
}

static async Task<int> ApplyManagerTasksAsync()
{
    var config = await LoadConfigurationAsync();
    if (config is null)
    {
        return 3;
    }

    await DeleteLegacyScheduledTasksAsync();
    BrokerRuntime.SignalReload();
    return 0;
}

static async Task<int> ApplyConfigurationAndReloadAsync()
{
    var result = await ApplyConfigurationAsync(serviceName);
    if (result == 0)
    {
        BrokerRuntime.SignalReload();
    }

    return result;
}

static async Task DeleteLegacyScheduledTasksAsync()
{
    await DeleteScheduledTaskAsync(updateTaskName);
    await DeleteScheduledTaskAsync(agentLogRotateTaskName);
    await DeleteScheduledTaskAsync(restartTaskName);
}

static async Task<int> RunScheduledAgentUpdateAsync()
{
    var config = await LoadConfigurationAsync();
    if (config is null || !IsAutoUpdateEnabled(config.RootElement))
    {
        return 0;
    }

    var intervalHours = GetUpdateIntervalHours(config.RootElement);
    var stampPath = Path.Combine(ProgramDataPath(), "BeszelAgentManager", autoUpdateStampName);
    if (File.Exists(stampPath))
    {
        var lastRun = File.GetLastWriteTimeUtc(stampPath);
        if (DateTime.UtcNow - lastRun < TimeSpan.FromHours(intervalHours))
        {
            return 0;
        }
    }

    Directory.CreateDirectory(Path.GetDirectoryName(stampPath)!);
    await File.WriteAllTextAsync(stampPath, DateTime.UtcNow.ToString("O"));
    return await UpdateAgentIfNewerAsync(config.RootElement);
}

static async Task<int> UpdateAgentIfNewerAsync(JsonElement config)
{
    var agentPath = AgentPath();
    if (!File.Exists(agentPath))
    {
        return await InstallOrUpdateAgentAsync();
    }

    var release = await FetchLatestAgentReleaseAsync();
    if (release is null)
    {
        return 20;
    }

    var installed = await GetInstalledAgentVersionAsync(agentPath);
    if (!IsUpdateAvailable(installed, release.Value.Version))
    {
        return 0;
    }

    await StopServiceAsync(serviceName);
    var installResult = await InstallAgentBinaryAsync(release.Value.Version, release.Value.DownloadUrl);
    if (installResult != 0)
    {
        return installResult;
    }

    return await ConfigureServiceAsync(serviceName, agentPath, config, restart: true);
}

static async Task<string> GetInstalledAgentVersionAsync(string agentPath)
{
    foreach (var argument in new[] { "--version", "version", "-version" })
    {
        try
        {
            var result = await RunProcessWithTimeoutAsync(
                agentPath,
                [argument],
                TimeSpan.FromSeconds(10));
            var match = Regex.Match(result.Output, @"\d+\.\d+\.\d+");
            if (match.Success)
            {
                return match.Value;
            }
        }
        catch
        {
        }
    }

    var info = FileVersionInfo.GetVersionInfo(agentPath);
    return NormalizeVersion(info.ProductVersion ?? info.FileVersion);
}

static bool IsUpdateAvailable(string currentVersion, string latestVersion)
{
    var current = ToVersionTuple(currentVersion);
    var latest = ToVersionTuple(latestVersion);
    if (latest.Item1 != current.Item1)
    {
        return latest.Item1 > current.Item1;
    }

    if (latest.Item2 != current.Item2)
    {
        return latest.Item2 > current.Item2;
    }

    return latest.Item3 > current.Item3;
}

static Tuple<int, int, int> ToVersionTuple(string value)
{
    var parts = NormalizeVersion(value).Split('.', StringSplitOptions.RemoveEmptyEntries);
    return Tuple.Create(ParseVersionPart(parts, 0), ParseVersionPart(parts, 1), ParseVersionPart(parts, 2));
}

static int ParseVersionPart(string[] parts, int index)
{
    return index < parts.Length && int.TryParse(parts[index], out var parsed) ? parsed : 0;
}

static bool IsAutoUpdateEnabled(JsonElement config)
{
    return !config.TryGetProperty("auto_update_enabled", out var property)
        || property.ValueKind != JsonValueKind.False;
}

static int GetUpdateIntervalHours(JsonElement config)
{
    if (!config.TryGetProperty("update_interval_hours", out var property))
    {
        return 24;
    }

    var value = property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var numeric)
        ? numeric
        : property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var textValue)
            ? textValue
            : 24;
    return Math.Clamp(value, 1, 720);
}

static async Task<int> RunAgentProcessAsync(string agentPath, string[] arguments, JsonElement config)
{
    try
    {
        using var process = new Process();
        process.StartInfo.FileName = agentPath;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        foreach (var item in BuildAgentEnvironment(config))
        {
            var separator = item.IndexOf('=');
            if (separator > 0)
            {
                process.StartInfo.Environment[item[..separator]] = item[(separator + 1)..];
            }
        }

        process.Start();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 ? 0 : 4;
    }
    catch
    {
        return 4;
    }
}

static async Task<int> InstallOrUpdateAgentAsync()
{
    var release = await FetchLatestAgentReleaseAsync();
    if (release is null)
    {
        return 20;
    }

    await StopServiceAsync(serviceName);
    var installResult = await InstallAgentBinaryAsync(release.Value.Version, release.Value.DownloadUrl);
    if (installResult != 0)
    {
        return installResult;
    }

    return await ApplyConfigurationAsync(serviceName);
}

static async Task<int> UpdateAgentAsync(string? version)
{
    AgentRelease? release = string.IsNullOrWhiteSpace(version)
        ? await FetchLatestAgentReleaseAsync()
        : await FetchAgentReleaseAsync(version);
    if (release is null)
    {
        return 20;
    }

    await StopServiceAsync(serviceName);
    var installResult = await InstallAgentBinaryAsync(release.Value.Version, release.Value.DownloadUrl);
    if (installResult != 0)
    {
        return installResult;
    }

    return await StartServiceAsync(serviceName);
}

static async Task<int> UninstallAgentAsync(bool removeAgentLogs)
{
    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    var agentDir = Path.Combine(programFiles, "Beszel-Agent");
    var legacyAgentDir = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Beszel-Agent");
    var dataDir = Path.Combine(programData, "BeszelAgentManager");
    var agentLogDir = Path.Combine(dataDir, "agent_logs");
    var agentTempDir = Path.Combine(dataDir, "tmp_agent");

    await StopServiceAsync(serviceName);
    await DeleteScheduledTaskAsync(updateTaskName);
    await DeleteScheduledTaskAsync(agentLogRotateTaskName);
    await DeleteScheduledTaskAsync(restartTaskName);

    var nssmPath = FindNssmPath();
    if (nssmPath is not null)
    {
        await RunProcessAsync(nssmPath, ["remove", serviceName, "confirm"]);
        await RunProcessAsync(nssmPath, ["remove", legacyServiceName, "confirm"]);
    }

    await RunProcessAsync("sc.exe", ["delete", serviceName]);
    await RunProcessAsync("sc.exe", ["delete", legacyServiceName]);
    await DeleteFirewallRuleAsync();

    var removed = TryDeleteDirectory(agentDir)
        && TryDeleteDirectory(legacyAgentDir)
        && TryDeleteDirectory(agentTempDir);
    if (removeAgentLogs)
    {
        removed = removed && TryDeleteDirectory(agentLogDir);
    }

    if (!removed)
    {
        return 23;
    }

    return 0;
}

static Task DeleteScheduledTaskAsync(string taskName)
{
    return RunProcessAsync("schtasks.exe", ["/Delete", "/TN", taskName, "/F"]);
}

static bool TryDeleteDirectory(string path)
{
    try
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        _ = RunProcessAsync("takeown.exe", ["/f", path, "/r", "/d", "y"]).GetAwaiter().GetResult();
        _ = RunProcessAsync("icacls.exe", [path, "/grant", "*S-1-5-32-544:(OI)(CI)F", "/T", "/C"]).GetAwaiter().GetResult();
        _ = RunProcessAsync("icacls.exe", [path, "/grant", "*S-1-5-18:(OI)(CI)F", "/T", "/C"]).GetAwaiter().GetResult();
        Directory.Delete(path, recursive: true);
        return true;
    }
    catch
    {
        return !Directory.Exists(path);
    }
}

static async Task<int> ConfigureServiceAsync(
    string serviceName,
    string agentPath,
    JsonElement config,
    bool restart,
    string? hubUrlOverride = null)
{
    var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    var nssmPath = FindNssmPath();
    if (nssmPath is null)
    {
        return 53;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(agentPath)!);
    Directory.CreateDirectory(Path.Combine(programData, "BeszelAgentManager"));
    Directory.CreateDirectory(Path.Combine(programData, "BeszelAgentManager", "agent_logs"));

    var existedAtStart = await ServiceExistsAsync(serviceName);
    var initialState = existedAtStart ? (await QueryServiceAsync(serviceName)).State : "NOT FOUND";
    var createdService = false;
    string? originalServiceBinaryPath = null;
    var serviceBinaryPathChanged = false;
    var snapshots = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
    var changed = new List<string>();

    if (!await ServiceExistsAsync(serviceName))
    {
        var install = await RunServiceCommandAsync(nssmPath, ["install", serviceName, agentPath], "NSSM service install");
        if (!install)
        {
            return 4;
        }

        createdService = !existedAtStart;
        if (!await WaitUntilServiceExistsAsync(serviceName, TimeSpan.FromSeconds(5)))
        {
            await RemoveNewServiceAsync(nssmPath, serviceName);
            return 4;
        }
    }

    originalServiceBinaryPath = ReadServiceBinaryPath(serviceName);
    if (!string.IsNullOrWhiteSpace(originalServiceBinaryPath)
        && !string.Equals(
            NormalizeServiceBinaryPath(originalServiceBinaryPath),
            Path.GetFullPath(nssmPath),
            StringComparison.OrdinalIgnoreCase))
    {
        var migrateBinaryPath = await RunProcessAsync(
            "sc.exe",
            ["config", serviceName, "binPath=", $"\"{Path.GetFullPath(nssmPath)}\""]);
        if (migrateBinaryPath.ExitCode != 0
            || !string.Equals(
                NormalizeServiceBinaryPath(ReadServiceBinaryPath(serviceName)),
                Path.GetFullPath(nssmPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }
        serviceBinaryPathChanged = true;
    }

    var logPath = Path.Combine(programData, "BeszelAgentManager", "agent_logs", "beszel-agent.log");
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

    var environment = BuildAgentEnvironment(config);
    if (!string.IsNullOrWhiteSpace(hubUrlOverride))
    {
        environment =
        [
            .. environment.Where(static value => !value.StartsWith("HUB_URL=", StringComparison.OrdinalIgnoreCase)),
            $"HUB_URL={hubUrlOverride.Trim()}",
        ];
    }
    try
    {
        foreach (var (parameter, desired) in DesiredServiceSettings(agentPath, logPath, environment))
        {
            if (!await ApplyNssmParameterAsync(nssmPath, serviceName, parameter, desired, snapshots, changed))
            {
                throw new InvalidOperationException($"Service setting verification failed for {parameter}.");
            }
        }

        if (TryGetListenPort(config, out var listenPort))
        {
            await EnsureFirewallRuleAsync(listenPort);
        }
        else
        {
            await DeleteFirewallRuleAsync();
        }

        if (!restart)
        {
            return 0;
        }

        var restartResult = await RestartServiceAsync(serviceName);
        if (restartResult != 0)
        {
            throw new InvalidOperationException($"Service restart failed with code {restartResult}.");
        }

        if (!await RequireServiceStaysRunningAsync(serviceName, TimeSpan.FromSeconds(3)))
        {
            throw new InvalidOperationException("Service did not remain running after configuration.");
        }

        return 0;
    }
    catch (Exception ex)
    {
        WriteHelperLastError(ex);
        if (createdService)
        {
            await RemoveNewServiceAsync(nssmPath, serviceName);
        }
        else
        {
            await RollbackNssmParametersAsync(nssmPath, serviceName, snapshots, changed);
            if (serviceBinaryPathChanged && !string.IsNullOrWhiteSpace(originalServiceBinaryPath))
            {
                await RunProcessAsync("sc.exe", ["config", serviceName, "binPath=", originalServiceBinaryPath]);
            }
            if (string.Equals(initialState, "RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                await RestartServiceAsync(serviceName);
            }
            else
            {
                await StopServiceAsync(serviceName);
            }
        }

        return 4;
    }
}

static string? ReadServiceBinaryPath(string serviceName)
{
    using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
    return key?.GetValue("ImagePath")?.ToString();
}

static string NormalizeServiceBinaryPath(string? binaryPath)
{
    return (binaryPath ?? string.Empty).Trim().Trim('"');
}

static void WriteHelperLastError(Exception exception)
{
    try
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var directory = Path.Combine(programData, "BeszelAgentManager");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "helper-last-error.txt"), exception.ToString());
    }
    catch
    {
    }
}

static IEnumerable<(string Parameter, string[] Desired)> DesiredServiceSettings(string agentPath, string logPath, string[] environment)
{
    yield return ("Description", ["Beszel agent service managed by BeszelAgentManager."]);
    yield return ("Application", [agentPath]);
    yield return ("AppDirectory", [Path.GetDirectoryName(agentPath)!]);
    yield return ("AppStdout", [logPath]);
    yield return ("AppStderr", [logPath]);
    yield return ("AppRotateFiles", ["1"]);
    yield return ("AppRotateOnline", ["1"]);
    yield return ("AppStopMethodConsole", ["1500"]);
    yield return ("AppStopMethodWindow", ["1500"]);
    yield return ("AppStopMethodThreads", ["1500"]);
    yield return ("AppEnvironment", []);
    yield return ("AppEnvironmentExtra", environment.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    yield return ("Start", ["SERVICE_AUTO_START"]);
    yield return ("AppRestartDelay", ["5000"]);
}

static async Task<bool> ApplyNssmParameterAsync(
    string nssmPath,
    string serviceName,
    string parameter,
    string[] desired,
    Dictionary<string, string[]?> snapshots,
    List<string> changed)
{
    var current = await GetNssmParameterAsync(nssmPath, serviceName, parameter);
    snapshots[parameter] = current;
    if (ParameterMatches(parameter, current, desired))
    {
        return true;
    }

    if (!await WriteNssmParameterAsync(nssmPath, serviceName, parameter, desired, $"Set service {parameter}"))
    {
        return false;
    }

    changed.Add(parameter);
    var verified = await GetNssmParameterAsync(nssmPath, serviceName, parameter);
    return ParameterMatches(parameter, verified, desired);
}

static async Task<string[]?> GetNssmParameterAsync(string nssmPath, string serviceName, string parameter)
{
    var result = await RunProcessAsync(nssmPath, ["get", serviceName, parameter]);
    if (result.ExitCode != 0)
    {
        return null;
    }

    return result.Output
        .Replace("\0", string.Empty)
        .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(static line => !string.IsNullOrWhiteSpace(line))
        .ToArray();
}

static async Task<bool> WriteNssmParameterAsync(
    string nssmPath,
    string serviceName,
    string parameter,
    string[]? values,
    string description)
{
    return values is { Length: > 0 }
        ? await RunServiceCommandAsync(nssmPath, ["set", serviceName, parameter, .. values], description)
        : await RunServiceCommandAsync(nssmPath, ["reset", serviceName, parameter], description);
}

static async Task RollbackNssmParametersAsync(
    string nssmPath,
    string serviceName,
    Dictionary<string, string[]?> snapshots,
    List<string> changed)
{
    for (var index = changed.Count - 1; index >= 0; index--)
    {
        var parameter = changed[index];
        snapshots.TryGetValue(parameter, out var previous);
        try
        {
            await WriteNssmParameterAsync(nssmPath, serviceName, parameter, previous, $"Restore service {parameter}");
        }
        catch
        {
            // Best-effort rollback; the caller already reports configuration failure.
        }
    }
}

static bool ParameterMatches(string parameter, string[]? actual, string[] desired)
{
    return NormalizeParameterValues(parameter, actual).SequenceEqual(NormalizeParameterValues(parameter, desired));
}

static IEnumerable<string> NormalizeParameterValues(string parameter, IEnumerable<string>? values)
{
    if (values is null)
    {
        return [];
    }

    var normalized = values
        .Select(static value => value.Trim().Trim('"'))
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .ToList();
    if (parameter is "Application" or "AppDirectory" or "AppStdout" or "AppStderr")
    {
        return normalized.Select(static value => Path.GetFullPath(value).TrimEnd('\\').ToLowerInvariant());
    }

    if (parameter is "AppEnvironment" or "AppEnvironmentExtra")
    {
        return normalized.Order(StringComparer.OrdinalIgnoreCase);
    }

    return normalized;
}

static async Task<bool> RunServiceCommandAsync(string fileName, string[] arguments, string description, int attempts = 3)
{
    attempts = Math.Max(1, attempts);
    for (var attempt = 1; attempt <= attempts; attempt++)
    {
        var result = await RunProcessAsync(fileName, arguments);
        if (result.ExitCode == 0)
        {
            return true;
        }

        if (attempt < attempts && IsTransientServiceError(result.Output))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750 * attempt));
            continue;
        }

        return false;
    }

    return false;
}

static bool IsTransientServiceError(string text)
{
    return text.Contains("marked for deletion", StringComparison.OrdinalIgnoreCase)
        || text.Contains("error 1072", StringComparison.OrdinalIgnoreCase)
        || text.Contains("failed 1072", StringComparison.OrdinalIgnoreCase)
        || text.Contains("database is locked", StringComparison.OrdinalIgnoreCase);
}

static async Task RemoveNewServiceAsync(string nssmPath, string serviceName)
{
    await RunProcessAsync(nssmPath, ["stop", serviceName]);
    await RunProcessAsync(nssmPath, ["remove", serviceName, "confirm"]);
    await RunProcessAsync("sc.exe", ["delete", serviceName]);
}

static async Task<bool> WaitUntilServiceExistsAsync(string serviceName, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (await ServiceExistsAsync(serviceName))
        {
            return true;
        }

        await Task.Delay(500);
    }

    return await ServiceExistsAsync(serviceName);
}

static async Task<bool> RequireServiceStaysRunningAsync(string serviceName, TimeSpan stability)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
    DateTime? runningSince = null;
    while (DateTime.UtcNow < deadline)
    {
        var snapshot = await QueryServiceAsync(serviceName);
        if (!snapshot.Exists)
        {
            return false;
        }

        if (string.Equals(snapshot.State, "RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            runningSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - runningSince >= stability)
            {
                return true;
            }
        }
        else
        {
            runningSince = null;
        }

        await Task.Delay(500);
    }

    return false;
}

static string[] BuildAgentEnvironment(JsonElement config)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    AddValue("KEY", "key");
    AddValue("TOKEN", "token");
    AddValue("HUB_URL", "hub_url");
    AddValue("LISTEN", "listen");

    var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["DATA_DIR"] = "data_dir",
        ["DOCKER_HOST"] = "docker_host",
        ["EXCLUDE_CONTAINERS"] = "exclude_containers",
        ["EXCLUDE_SMART"] = "exclude_smart",
        ["EXTRA_FILESYSTEMS"] = "extra_filesystems",
        ["FILESYSTEM"] = "filesystem",
        ["INTEL_GPU_DEVICE"] = "intel_gpu_device",
        ["NVML"] = "nvml",
        ["KEY_FILE"] = "key_file",
        ["TOKEN_FILE"] = "token_file",
        ["LHM"] = "lhm",
        ["LOG_LEVEL"] = "log_level",
        ["MEM_CALC"] = "mem_calc",
        ["NETWORK"] = "network",
        ["NICS"] = "nics",
        ["SENSORS"] = "sensors",
        ["SENSORS_TIMEOUT"] = "sensors_timeout",
        ["PRIMARY_SENSOR"] = "primary_sensor",
        ["SYS_SENSORS"] = "sys_sensors",
        ["SERVICE_PATTERNS"] = "service_patterns",
        ["SMART_DEVICES"] = "smart_devices",
        ["SMART_INTERVAL"] = "smart_interval",
        ["SYSTEM_NAME"] = "system_name",
        ["SKIP_GPU"] = "skip_gpu",
        ["GPU_COLLECTOR"] = "gpu_collector",
        ["DISABLE_SSH"] = "disable_ssh",
        ["DISK_USAGE_CACHE"] = "disk_usage_cache",
        ["SKIP_SYSTEMD"] = "skip_systemd",
    };

    if (config.TryGetProperty("env_active_names", out var activeNames)
        && activeNames.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in activeNames.EnumerateArray())
        {
            var environmentName = item.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(environmentName)
                || !mapping.TryGetValue(environmentName, out var configName))
            {
                continue;
            }

            AddValue(environmentName, configName);
        }
    }

    if (config.TryGetProperty("env_custom", out var custom)
        && custom.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in custom.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var nameProperty)
                || !item.TryGetProperty("value", out var valueProperty))
            {
                continue;
            }

            var name = nameProperty.GetString()?.Trim();
            var value = valueProperty.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(name)
                && !string.IsNullOrWhiteSpace(value)
                && Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)
                && value.IndexOfAny(['\0', '\r', '\n']) < 0
                && name is not ("KEY" or "TOKEN" or "HUB_URL" or "LISTEN")
                && !values.ContainsKey(name))
            {
                values[name] = value;
            }
        }
    }

    return values.Select(pair => $"{pair.Key}={pair.Value}").ToArray();

    void AddValue(string environmentName, string configName)
    {
        if (!config.TryGetProperty(configName, out var property))
        {
            return;
        }

        var value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim(),
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => "1",
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[environmentName] = value;
        }
    }
}

static async Task<(bool Exists, string State, int Pid)> QueryServiceAsync(string name)
{
    var result = await RunProcessAsync("sc.exe", ["queryex", name]);
    if (ServiceQueryReportsMissing(result))
    {
        return (false, string.Empty, 0);
    }

    if (result.ExitCode != 0)
    {
        return (false, string.Empty, 0);
    }

    var state = ParseServiceState(result.Output);
    var pid = 0;
    using var reader = new StringReader(result.Output);
    while (reader.ReadLine() is { } line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("PID", StringComparison.OrdinalIgnoreCase))
        {
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 2 && int.TryParse(parts[2], out var parsedPid))
            {
                pid = parsedPid;
            }
        }
    }

    return (true, state, pid);
}

static bool ServiceQueryReportsMissing((int ExitCode, string Output) result)
{
    if (result.ExitCode == 0)
    {
        return false;
    }

    return result.Output.Contains("1060", StringComparison.OrdinalIgnoreCase)
        || result.Output.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
}

static string ParseServiceState(string output)
{
    var stateMatch = Regex.Match(output, @"STATE\s*:\s*(\d+)", RegexOptions.IgnoreCase);
    if (stateMatch.Success && int.TryParse(stateMatch.Groups[1].Value, out var stateCode))
    {
        return stateCode switch
        {
            1 => "STOPPED",
            2 => "START_PENDING",
            3 => "STOP_PENDING",
            4 => "RUNNING",
            5 => "CONTINUE_PENDING",
            6 => "PAUSE_PENDING",
            7 => "PAUSED",
            _ => "UNKNOWN",
        };
    }

    foreach (var state in new[]
    {
        "STOPPED",
        "START_PENDING",
        "STOP_PENDING",
        "RUNNING",
        "CONTINUE_PENDING",
        "PAUSE_PENDING",
        "PAUSED",
    })
    {
        if (output.Contains(state, StringComparison.OrdinalIgnoreCase))
        {
            return state;
        }
    }

    return "UNKNOWN";
}

static bool IsStoppedState(string state)
{
    return string.Equals(state, "STOPPED", StringComparison.OrdinalIgnoreCase);
}

static async Task<bool> ServiceExistsAsync(string name)
{
    return (await QueryServiceAsync(name)).Exists;
}

static async Task<int> InstallAgentBinaryAsync(string version, string downloadUrl)
{
    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    var agentDir = Path.Combine(programFiles, "Beszel-Agent");
    var agentPath = Path.Combine(agentDir, "beszel-agent.exe");
    var tempDir = Path.Combine(programData, "BeszelAgentManager", "tmp_agent");
    var zipPath = Path.Combine(tempDir, "beszel-agent.zip");
    var extractDir = Path.Combine(tempDir, "extract");

    try
    {
        Directory.CreateDirectory(agentDir);
        ResetPrivilegedWorkingDirectory(tempDir);
        Directory.CreateDirectory(extractDir);
        if (!IsTrustedGitHubAssetUrl(downloadUrl))
        {
            return 21;
        }

        using var http = CreateGitHubClient();
        await DownloadFileAsync(http, downloadUrl, zipPath, 250L * 1024 * 1024);

        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
        var extracted = Directory
            .EnumerateFiles(extractDir, "beszel-agent.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(extracted) || !File.Exists(extracted))
        {
            return 21;
        }

        if (File.Exists(agentPath))
        {
            File.SetAttributes(agentPath, FileAttributes.Normal);
            File.Delete(agentPath);
        }

        File.Move(extracted, agentPath, overwrite: true);
        return 0;
    }
    catch
    {
        return 22;
    }
    finally
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                ResetPrivilegedWorkingDirectory(tempDir);
                Directory.Delete(tempDir);
            }
        }
        catch
        {
        }
    }
}

static async Task<AgentRelease?> FetchLatestAgentReleaseAsync()
{
    using var http = CreateGitHubClient();
    await using var stream = await http.GetStreamAsync($"https://api.github.com/repos/{agentRepo}/releases/latest");
    using var document = await JsonDocument.ParseAsync(stream);
    return ParseAgentRelease(document.RootElement);
}

static async Task<int> InstallManagerVersionAsync(string tag)
{
    try
    {
        using var http = CreateGitHubClient();
        var releaseUrl = $"https://api.github.com/repos/{managerRepo}/releases/tags/{Uri.EscapeDataString(tag)}";
        await using var releaseStream = await http.GetStreamAsync(releaseUrl);
        using var release = await JsonDocument.ParseAsync(releaseStream);
        if (!TryGetReleaseAssetUrl(release.RootElement, managerInstallerName, out var installerUrl)
            || !TryGetReleaseAssetUrl(release.RootElement, managerChecksumName, out var checksumUrl))
        {
            return 30;
        }

        var stagingRoot = Path.Combine(ProgramDataPath(), "BeszelAgentManager", "manager-update");
        ResetPrivilegedWorkingDirectory(stagingRoot);

        var installerPath = Path.Combine(stagingRoot, managerInstallerName);
        var checksumPath = Path.Combine(stagingRoot, managerChecksumName);
        await DownloadFileAsync(http, installerUrl, installerPath, 512L * 1024 * 1024);
        await DownloadFileAsync(http, checksumUrl, checksumPath, 1024 * 1024);
        if (new FileInfo(installerPath).Attributes.HasFlag(FileAttributes.ReparsePoint)
            || !VerifyManagerInstallerChecksum(installerPath, checksumPath)
            || !await VerifyManagerInstallerSignatureAsync(installerPath))
        {
            TryDeleteFile(installerPath);
            return 31;
        }

        var escapedInstaller = installerPath.Replace("'", "''", StringComparison.Ordinal);
        var command =
            $"Start-Sleep -Seconds 3; Start-Process -FilePath '{escapedInstaller}' " +
            "-ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CLOSEAPPLICATIONS')";
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-NoProfile",
                "-NonInteractive",
                "-WindowStyle", "Hidden",
                "-Command", command,
            },
        });
        return 0;
    }
    catch (HttpRequestException)
    {
        return 30;
    }
    catch (Exception ex)
    {
        WriteBackgroundLog("ERROR", $"Manager installer staging failed: {ex}");
        return 32;
    }
}

static void ResetPrivilegedWorkingDirectory(string path)
{
    var fullPath = Path.GetFullPath(path);
    if (Directory.Exists(fullPath))
    {
        SecurePrivilegedWorkingDirectory(fullPath);
    }
    else
    {
        Directory.CreateDirectory(fullPath);
        SecurePrivilegedWorkingDirectory(fullPath);
    }

    foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath))
    {
        if (Directory.Exists(entry))
        {
            Directory.Delete(entry, recursive: true);
        }
        else
        {
            File.Delete(entry);
        }
    }

}

static void SecurePrivilegedWorkingDirectory(string path)
{
    var fullPath = Path.GetFullPath(path);
    var managerData = Path.GetFullPath(Path.Combine(ProgramDataPath(), "BeszelAgentManager"))
        .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!fullPath.StartsWith(managerData, StringComparison.OrdinalIgnoreCase))
    {
        throw new IOException("Privileged working directory is outside manager data.");
    }
    if (!Directory.Exists(fullPath)
        || File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
    {
        throw new IOException("Privileged working directory is missing or is a reparse point.");
    }

    SecureAndValidate(fullPath);

    void SecureAndValidate(string directory)
    {
        ApplyRestrictedDirectorySecurity(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"Reparse point rejected in privileged working directory: {entry}");
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                SecureAndValidate(entry);
            }
            else
            {
                ApplyRestrictedFileSecurity(entry);
            }
        }
    }
}

static void ApplyRestrictedDirectorySecurity(string path)
{
    var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    var security = new DirectorySecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    security.SetOwner(administrators);
    security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
    security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
    new DirectoryInfo(path).SetAccessControl(security);
}

static void ApplyRestrictedFileSecurity(string path)
{
    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    var security = new FileSecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    security.SetOwner(administrators);
    security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
    security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, AccessControlType.Allow));
    new FileInfo(path).SetAccessControl(security);
}

static bool TryGetReleaseAssetUrl(JsonElement release, string expectedName, out string url)
{
    url = string.Empty;
    if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
    {
        return false;
    }

    foreach (var asset in assets.EnumerateArray())
    {
        var name = asset.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
        if (!string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        url = asset.TryGetProperty("browser_download_url", out var urlProperty)
            ? urlProperty.GetString() ?? string.Empty
            : string.Empty;
        return IsTrustedGitHubAssetUrl(url);
    }

    return false;
}

static bool IsTrustedGitHubAssetUrl(string url)
{
    return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(parsed.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && parsed.AbsolutePath.StartsWith("/", StringComparison.Ordinal);
}

static async Task DownloadFileAsync(HttpClient http, string url, string path, long maximumBytes)
{
    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maximumBytes)
    {
        throw new IOException("Download exceeds the allowed size.");
    }

    await using var source = await response.Content.ReadAsStreamAsync();
    await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
    var buffer = new byte[64 * 1024];
    long total = 0;
    while (true)
    {
        var read = await source.ReadAsync(buffer);
        if (read == 0)
        {
            break;
        }

        total += read;
        if (total > maximumBytes)
        {
            throw new IOException("Download exceeds the allowed size.");
        }

        await destination.WriteAsync(buffer.AsMemory(0, read));
    }
}

static bool VerifyManagerInstallerChecksum(string installerPath, string checksumPath)
{
    var expected = File.ReadLines(checksumPath)
        .Select(static line => Regex.Match(line, @"^\s*([a-fA-F0-9]{64})\s+\*?BeszelAgentManagerSetup\.exe\s*$"))
        .FirstOrDefault(static match => match.Success)?
        .Groups[1].Value;
    if (string.IsNullOrWhiteSpace(expected))
    {
        return false;
    }

    using var stream = File.OpenRead(installerPath);
    var actual = Convert.ToHexString(SHA256.HashData(stream));
    return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
}

static async Task<bool> VerifyManagerInstallerSignatureAsync(string installerPath)
{
    var escapedPath = installerPath.Replace("'", "''", StringComparison.Ordinal);
    var result = await RunProcessAsync(
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        [
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            $"(Get-AuthenticodeSignature -LiteralPath '{escapedPath}').Status.ToString()",
        ]);
    var status = result.Output.Trim();
    return result.ExitCode == 0
        && (string.Equals(status, "Valid", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "NotSigned", StringComparison.OrdinalIgnoreCase));
}

static async Task<AgentRelease?> FetchAgentReleaseAsync(string version)
{
    var normalized = NormalizeVersion(version);
    using var http = CreateGitHubClient();
    await using var stream = await http.GetStreamAsync($"https://api.github.com/repos/{agentRepo}/releases?per_page=100");
    using var document = await JsonDocument.ParseAsync(stream);
    if (document.RootElement.ValueKind != JsonValueKind.Array)
    {
        return null;
    }

    foreach (var release in document.RootElement.EnumerateArray())
    {
        var parsed = ParseAgentRelease(release);
        if (parsed is not null && string.Equals(parsed.Value.Version, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return parsed;
        }
    }

    return null;
}

static AgentRelease? ParseAgentRelease(JsonElement release)
{
    if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
    {
        return null;
    }

    if (release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
    {
        return null;
    }

    var tag = release.TryGetProperty("tag_name", out var tagProperty)
        ? tagProperty.GetString() ?? string.Empty
        : string.Empty;
    var version = NormalizeVersion(tag);
    if (string.IsNullOrWhiteSpace(version)
        || !release.TryGetProperty("assets", out var assets)
        || assets.ValueKind != JsonValueKind.Array)
    {
        return null;
    }

    foreach (var asset in assets.EnumerateArray())
    {
        var name = asset.TryGetProperty("name", out var nameProperty)
            ? nameProperty.GetString() ?? string.Empty
            : string.Empty;
        if (!string.Equals(name, agentZipName, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProperty)
            ? urlProperty.GetString() ?? string.Empty
            : string.Empty;
        return IsTrustedGitHubAssetUrl(downloadUrl) ? new AgentRelease(version, downloadUrl) : null;
    }

    return null;
}

static HttpClient CreateGitHubClient()
{
    var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BeszelAgentManager", "4.0.0"));
    return http;
}

static string NormalizeVersion(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    var trimmed = value.Trim();
    if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
    {
        trimmed = trimmed[1..].Trim();
    }

    var match = Regex.Match(trimmed, @"\d+\.\d+\.\d+");
    return match.Success ? match.Value : trimmed;
}

static bool TryGetListenPort(JsonElement config, out int port)
{
    port = 0;
    if (!config.TryGetProperty("listen", out var listen))
    {
        return false;
    }

    if (listen.ValueKind == JsonValueKind.Number && listen.TryGetInt32(out port))
    {
        return port is >= 1 and <= 65535;
    }

    if (listen.ValueKind == JsonValueKind.String && int.TryParse(listen.GetString(), out port))
    {
        return port is >= 1 and <= 65535;
    }

    return false;
}

static Task EnsureFirewallRuleAsync(int port)
{
    return RunProcessAsync(
        "netsh.exe",
        ["advfirewall", "firewall", "add", "rule", $"name={firewallRuleName}", "dir=in", "action=allow", "protocol=TCP", $"localport={port}"]);
}

static Task DeleteFirewallRuleAsync()
{
    return RunProcessAsync("netsh.exe", ["advfirewall", "firewall", "delete", "rule", $"name={firewallRuleName}"]);
}

static async Task<int> RotateAgentLogsAsync()
{
    var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    var logDir = Path.Combine(programData, "BeszelAgentManager", "agent_logs");
    var currentLog = Path.Combine(logDir, "beszel-agent.log");
    Directory.CreateDirectory(logDir);

    var before = Directory
        .EnumerateFiles(logDir)
        .Select(Path.GetFileName)
        .Where(static name => !string.IsNullOrWhiteSpace(name))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var nssmPath = FindNssmPath();
    if (nssmPath is not null && await ServiceExistsAsync(serviceName))
    {
        await RunProcessAsync(nssmPath, ["rotate", serviceName]);
    }

    var deadline = DateTime.UtcNow.AddSeconds(4);
    while (DateTime.UtcNow < deadline)
    {
        var rotated = Directory
            .EnumerateFiles(logDir, "beszel-agent-*.log")
            .Where(path => !before.Contains(Path.GetFileName(path)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(rotated))
        {
            var target = UniqueDailyAgentLogPath(logDir, File.GetLastWriteTime(rotated).Date);
            File.Move(rotated, target, overwrite: false);
            return 0;
        }

        await Task.Delay(250);
    }

    if (!File.Exists(currentLog) || new FileInfo(currentLog).Length == 0)
    {
        File.Open(currentLog, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose();
        return 0;
    }

    try
    {
        var target = UniqueDailyAgentLogPath(logDir, DateTime.Today);
        await CopySharedTextFileAsync(currentLog, target);
        TruncateSharedFile(currentLog);
        return 0;
    }
    catch
    {
        return 24;
    }
}

static async Task CopySharedTextFileAsync(string sourcePath, string targetPath)
{
    await using var source = new FileStream(
        sourcePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 16 * 1024,
        useAsync: true);
    await using var target = new FileStream(
        targetPath,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.Read,
        bufferSize: 16 * 1024,
        useAsync: true);
    await source.CopyToAsync(target);
}

static void TruncateSharedFile(string path)
{
    using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Write,
        FileShare.ReadWrite | FileShare.Delete);
    stream.SetLength(0);
}

static string UniqueDailyAgentLogPath(string logDir, DateTime day)
{
    var basePath = Path.Combine(logDir, $"{day:yyyy-MM-dd}.txt");
    if (!File.Exists(basePath))
    {
        return basePath;
    }

    for (var index = 2; index < 50; index++)
    {
        var candidate = Path.Combine(logDir, $"{day:yyyy-MM-dd}_{index}.txt");
        if (!File.Exists(candidate))
        {
            return candidate;
        }
    }

    return Path.Combine(logDir, $"{day:yyyy-MM-dd}_{DateTime.Now:HHmmss}.txt");
}

static async Task<int> RunScAsync(string command, string name)
{
    var result = await RunProcessAsync("sc.exe", [command, name]);
    return result.ExitCode;
}

static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string[] arguments)
{
    if (!Path.IsPathRooted(fileName))
    {
        fileName = fileName.ToLowerInvariant() switch
        {
            "icacls.exe" or "netsh.exe" or "sc.exe" or "schtasks.exe" or "takeown.exe" or "taskkill.exe"
                => Path.Combine(Environment.SystemDirectory, fileName),
            _ => throw new InvalidOperationException($"System executable is not allowlisted: {fileName}"),
        };
    }

    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };

    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo);
    if (process is null)
    {
        return (1, "Could not start process.");
    }

    var stdout = await process.StandardOutput.ReadToEndAsync();
    var stderr = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode, $"{stdout}{Environment.NewLine}{stderr}".Trim());
}

static async Task<(int ExitCode, string Output)> RunProcessWithTimeoutAsync(
    string fileName,
    string[] arguments,
    TimeSpan timeout)
{
    if (!Path.IsPathRooted(fileName))
    {
        throw new InvalidOperationException("A fully qualified executable path is required.");
    }

    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };

    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo);
    if (process is null)
    {
        return (1, "Could not start process.");
    }

    using var timeoutSource = new CancellationTokenSource(timeout);
    try
    {
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        await process.WaitForExitAsync(timeoutSource.Token);
        return (process.ExitCode, $"{await stdoutTask}{Environment.NewLine}{await stderrTask}".Trim());
    }
    catch (OperationCanceledException)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        return (1460, "The process timed out.");
    }
}

internal readonly record struct AgentRelease(string Version, string DownloadUrl);

internal sealed class BrokerPolicy
{
    public int ProtocolVersion { get; set; }
    public string AuthorizedSid { get; set; } = string.Empty;
}

internal static class BrokerRuntime
{
    public static SemaphoreSlim MutationGate { get; } = new(1, 1);
    public static SemaphoreSlim ReloadSignal { get; } = new(0, 1);

    public static void SignalReload()
    {
        if (ReloadSignal.CurrentCount == 0)
        {
            ReloadSignal.Release();
        }
    }
}

internal sealed class BackgroundRuntimeState
{
    public int AgentUpdateIntervalHours { get; set; } = 24;
    public DateTimeOffset? LastAgentUpdateAt { get; set; }
    public DateTimeOffset? NextAgentUpdateAt { get; set; }
    public int PeriodicRestartIntervalHours { get; set; } = 24;
    public int PeriodicRestartIntervalMinutes { get; set; }
    public DateTimeOffset? LastPeriodicRestartAt { get; set; }
    public DateTimeOffset? NextPeriodicRestartAt { get; set; }
    public DateTimeOffset? LastAgentLogRotationAt { get; set; }
    public DateTimeOffset? NextAgentLogRotationAt { get; set; }
    public string LastOperation { get; set; } = string.Empty;
}

internal sealed class BackgroundWindowsService : ServiceBase
{
    private readonly Func<CancellationToken, Task> _run;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;

    public BackgroundWindowsService(Func<CancellationToken, Task> run)
    {
        _run = run;
        ServiceName = "BeszelAgentManager Background";
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        _cancellation = new CancellationTokenSource();
        _worker = _run(_cancellation.Token);
        _ = _worker.ContinueWith(
            static task => Environment.Exit(1),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    protected override void OnStop()
    {
        StopWorker();
    }

    protected override void OnShutdown()
    {
        StopWorker();
        base.OnShutdown();
    }

    private void StopWorker()
    {
        _cancellation?.Cancel();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            _worker = null;
        }
    }
}
