using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Text;
using Microsoft.Win32;

const string serviceName = "Beszel Agent";
const string legacyServiceName = "BeszelAgentManager";
const string agentRepo = "henrygd/beszel";
const string agentZipName = "beszel-agent_windows_amd64.zip";
const string firewallRuleName = "Beszel Agent";
const string updateTaskName = "BeszelAgentManagerUpdate";
const string agentLogRotateTaskName = "BeszelAgentManagerAgentLogRotate";
const string restartTaskName = "BeszelAgentManagerRestartService";
const string backgroundServiceName = "BeszelAgentManager Background";
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

if (args.Length == 1 && string.Equals(args[0], "--remove-background-service", StringComparison.OrdinalIgnoreCase))
{
    return await RemoveBackgroundServiceAsync();
}

if (args.Length == 2 && string.Equals(args[0], "--apply-hub-url", StringComparison.OrdinalIgnoreCase))
{
    return await ApplyHubUrlOverrideAsync(serviceName, args[1]);
}

if (args.Length != 3
    || !string.Equals(args[0], "--server", StringComparison.OrdinalIgnoreCase)
    || !int.TryParse(args[2], out var parentProcessId))
{
    return 2;
}

var pipeName = args[1];
var pipeSecurity = CreatePipeSecurity();
while (IsProcessRunning(parentProcessId))
{
    await using var pipe = NamedPipeServerStreamAcl.Create(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous,
        0,
        0,
        pipeSecurity);

    using var waitCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try
    {
        await pipe.WaitForConnectionAsync(waitCancellation.Token);
    }
    catch (OperationCanceledException)
    {
        continue;
    }

    using var reader = new StreamReader(pipe, leaveOpen: true);
    await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
    var rawCommand = (await reader.ReadLineAsync())?.Trim() ?? string.Empty;
    var command = rawCommand.ToLowerInvariant();
    var exitCode = command switch
    {
        "service start" => await StartServiceAsync(serviceName),
        "service stop" => await StopServiceAsync(serviceName),
        "service restart" => await RestartServiceAsync(serviceName),
        "service edit" => OpenServiceEditor(serviceName),
        "config apply" => await ApplyConfigurationAsync(serviceName),
        _ when command.StartsWith("config hub-url ", StringComparison.Ordinal) =>
            await ApplyHubUrlOverrideAsync(serviceName, rawCommand["config hub-url ".Length..].Trim()),
        "manager tasks apply" => await ApplyManagerTasksAsync(),
        "agent install" => await InstallOrUpdateAgentAsync(),
        "agent update" => await UpdateAgentAsync(null),
        "agent uninstall" => await UninstallAgentAsync(removeAgentLogs: true),
        "agent uninstall keep-logs" => await UninstallAgentAsync(removeAgentLogs: false),
        "agent logs rotate" => await RotateAgentLogsAsync(),
        "agent fingerprint reset" => await ResetAgentFingerprintAsync(),
        _ when command.StartsWith("agent install-version ", StringComparison.Ordinal) =>
            await UpdateAgentAsync(rawCommand["agent install-version ".Length..].Trim()),
        _ => 2,
    };
    await writer.WriteLineAsync(exitCode.ToString());
}

return 0;

static PipeSecurity CreatePipeSecurity()
{
    var security = new PipeSecurity();
    security.AddAccessRule(new PipeAccessRule(
        new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
        PipeAccessRights.ReadWrite,
        AccessControlType.Allow));
    security.AddAccessRule(new PipeAccessRule(
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        PipeAccessRights.FullControl,
        AccessControlType.Allow));
    return security;
}

static bool IsProcessRunning(int processId)
{
    try
    {
        return !Process.GetProcessById(processId).HasExited;
    }
    catch
    {
        return false;
    }
}

static async Task<int> RestartServiceAsync(string name)
{
    var stopResult = await StopServiceAsync(name);
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

    var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
        var candidate = Path.Combine(directory.Trim(), "nssm.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return null;
}

static async Task<int> InstallOrUpdateBackgroundServiceAsync()
{
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

    return await RestartServiceAsync(backgroundServiceName);
}

static async Task<int> RemoveBackgroundServiceAsync()
{
    if (!await ServiceExistsAsync(backgroundServiceName))
    {
        return 0;
    }

    await StopServiceAsync(backgroundServiceName);
    await RunProcessAsync("sc.exe", ["delete", backgroundServiceName]);
    return 0;
}

static int RunBackgroundWindowsService()
{
    ServiceBase.Run(new BackgroundWindowsService(RunBackgroundLoopAsync));
    return 0;
}

static async Task RunBackgroundLoopAsync(CancellationToken cancellationToken)
{
    var state = LoadBackgroundState();
    WriteBackgroundLog("INFO", "Background service started");
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await CheckHubFailoverAsync(state, cancellationToken);
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
        }
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

static async Task CheckHubFailoverAsync(BackgroundState state, CancellationToken cancellationToken)
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
        && enabledValue.ValueKind == JsonValueKind.True
        && primary.Length > 0
        && fallback.Length > 0;

    if (!enabled)
    {
        if (state.Active && primary.Length > 0)
        {
            WriteBackgroundLog("INFO", "Hub URL IP fallback disabled. Switching Beszel Agent HUB_URL back to primary.");
            if (await ApplyBackgroundHubUrlAsync(primary, "primary"))
            {
                state.Active = false;
                state.SuccessStreak = 0;
            }
        }

        await WriteDnsFallbackStateAsync(root, state.Active, state.SuccessStreak);
        return;
    }

    var host = ExtractHost(primary);
    if (host.Length == 0)
    {
        await WriteDnsFallbackStateAsync(root, state.Active, state.SuccessStreak);
        return;
    }

    var dnsAvailable = await CanResolveHostAsync(host, cancellationToken);
    if (!state.Active)
    {
        if (!dnsAvailable)
        {
            WriteBackgroundLog("WARN", $"Hub DNS resolution failed for {host}. Switching Beszel Agent HUB_URL to fallback.");
            if (await ApplyBackgroundHubUrlAsync(fallback, "fallback"))
            {
                state.Active = true;
                state.SuccessStreak = 0;
            }
        }
    }
    else if (!dnsAvailable)
    {
        state.SuccessStreak = 0;
    }
    else
    {
        state.SuccessStreak++;
        if (state.SuccessStreak is 1 or 5)
        {
            WriteBackgroundLog("INFO", $"Hub DNS resolution recovered for {host} ({state.SuccessStreak}/5).");
        }

        if (state.SuccessStreak >= 5)
        {
            WriteBackgroundLog("INFO", "Hub DNS is stable again. Switching Beszel Agent HUB_URL back to primary.");
            if (await ApplyBackgroundHubUrlAsync(primary, "primary"))
            {
                state.Active = false;
                state.SuccessStreak = 0;
            }
        }
    }

    await WriteDnsFallbackStateAsync(root, state.Active, state.SuccessStreak);
}

static async Task<bool> ApplyBackgroundHubUrlAsync(string hubUrl, string destination)
{
    var encodedUrl = Convert.ToBase64String(Encoding.UTF8.GetBytes(hubUrl));
    var exitCode = await ApplyHubUrlOverrideAsync(serviceName, encodedUrl);
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

static BackgroundState LoadBackgroundState()
{
    try
    {
        var path = Path.Combine(ProgramDataPath(), "BeszelAgentManager", "dns-fallback-state.json");
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return new BackgroundState
            {
                Active = root.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True,
                SuccessStreak = root.TryGetProperty("success_streak", out var streak) && streak.TryGetInt32(out var value)
                    ? Math.Clamp(value, 0, 5)
                    : 0,
            };
        }
    }
    catch
    {
    }

    return new BackgroundState();
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
        return 0;
    }

    return result;
}

static async Task<int> StopServiceAsync(string name)
{
    var snapshot = await QueryServiceAsync(name);
    if (snapshot.Exists && IsStoppedState(snapshot.State))
    {
        return 0;
    }

    var result = await RunScAsync("stop", name);
    if (result == 0 || result == ServiceNotRunning)
    {
        return 0;
    }

    return result;
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

    return JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
}

static string ProgramDataPath()
{
    return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
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

    if (IsAutoUpdateEnabled(config.RootElement))
    {
        return await CreateOrUpdateAutoUpdateTaskAsync(GetUpdateIntervalHours(config.RootElement));
    }

    await DeleteScheduledTaskAsync(updateTaskName);
    return 0;
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
            var result = await RunProcessAsync(agentPath, [argument]);
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

static async Task<int> CreateOrUpdateAutoUpdateTaskAsync(int intervalHours)
{
    var helperPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BeszelAgentManager.Helper.exe");
    var action = $"\"{helperPath}\" --auto-update-agent";
    var result = await RunProcessAsync(
        "schtasks.exe",
        [
            "/Create",
            "/F",
            "/SC", "HOURLY",
            "/MO", "1",
            "/TN", updateTaskName,
            "/RL", "HIGHEST",
            "/RU", "SYSTEM",
            "/TR", action,
        ]);
    return result.ExitCode == 0 ? 0 : 4;
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

    return true;
}

static string[] BuildAgentEnvironment(JsonElement config)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    AddCore("KEY", "key");
    AddCore("TOKEN", "token");
    AddCore("HUB_URL", "hub_url");
    AddCore("LISTEN", "listen");

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
                && name is not ("KEY" or "TOKEN" or "HUB_URL" or "LISTEN")
                && !values.ContainsKey(name))
            {
                values[name] = value;
            }
        }
    }

    return values.Select(pair => $"{pair.Key}={pair.Value}").ToArray();

    void AddCore(string environmentName, string configName) => AddValue(environmentName, configName);

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
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }

        Directory.CreateDirectory(extractDir);
        using var http = CreateGitHubClient();
        await using (var input = await http.GetStreamAsync(downloadUrl))
        await using (var output = File.Create(zipPath))
        {
            await input.CopyToAsync(output);
        }

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
                Directory.Delete(tempDir, recursive: true);
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
        return string.IsNullOrWhiteSpace(downloadUrl) ? null : new AgentRelease(version, downloadUrl);
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

internal readonly record struct AgentRelease(string Version, string DownloadUrl);

internal sealed class BackgroundState
{
    public bool Active { get; set; }
    public int SuccessStreak { get; set; }
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
