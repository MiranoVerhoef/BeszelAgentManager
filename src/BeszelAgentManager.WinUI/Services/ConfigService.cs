using System.Text.Json;
using System.Text.Json.Nodes;

namespace BeszelAgentManager.WinUI.Services;

internal sealed class ConfigService
{
    public async Task<AgentConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ManagerPaths.ConfigPath))
        {
            return await ApplyLocalOverridesAsync(new AgentConfig(), cancellationToken);
        }

        try
        {
            await using var stream = new FileStream(ManagerPaths.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var config = Normalize(
                await JsonSerializer.DeserializeAsync(
                    stream,
                    AppJsonContext.Default.AgentConfig,
                    cancellationToken) ?? new AgentConfig());
            return await ApplyLocalOverridesAsync(config, cancellationToken);
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Could not load configuration from {ManagerPaths.ConfigPath}: {ex}");
            return await ApplyLocalOverridesAsync(new AgentConfig(), cancellationToken);
        }
    }

    public async Task SetDebugLoggingAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        JsonObject config;
        if (File.Exists(ManagerPaths.ConfigPath))
        {
            var raw = await File.ReadAllTextAsync(ManagerPaths.ConfigPath, cancellationToken);
            config = JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
        }
        else
        {
            config = new JsonObject();
        }

        config["debug_logging"] = enabled;
        try
        {
            Directory.CreateDirectory(ManagerPaths.DataDir);
            await File.WriteAllTextAsync(
                ManagerPaths.ConfigPath,
                config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            var local = new JsonObject { ["debug_logging"] = enabled };
            Directory.CreateDirectory(Path.GetDirectoryName(ManagerPaths.LocalSettingsPath)!);
            await File.WriteAllTextAsync(
                ManagerPaths.LocalSettingsPath,
                local.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
        }
    }

    public async Task SaveAsync(AgentConfig config, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ManagerPaths.DataDir);
        var temporaryPath = $"{ManagerPaths.ConfigPath}.{Environment.ProcessId}.tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            useAsync: true))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                config,
                AppJsonContext.Default.AgentConfig,
                cancellationToken);
        }

        File.Move(temporaryPath, ManagerPaths.ConfigPath, overwrite: true);
    }

    public IReadOnlyList<(string Name, string ConfigKey, string Value)> GetActiveEnvironmentRows(AgentConfig config)
    {
        var rows = new List<(string Name, string ConfigKey, string Value)>();
        var activeNames = config.EnvActiveNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var envName in activeNames)
        {
            var configKey = EnvNameToConfigKey(envName);
            var value = config.GetEnvironmentValue(configKey);
            rows.Add((envName, configKey, value));
        }

        foreach (var custom in config.EnvCustom)
        {
            if (string.IsNullOrWhiteSpace(custom.Name))
            {
                continue;
            }

            rows.Add((custom.Name.Trim(), custom.Name.Trim(), custom.Value));
        }

        return rows;
    }

    private static AgentConfig Normalize(AgentConfig config)
    {
        return new AgentConfig
        {
            Key = config.Key ?? string.Empty,
            Token = config.Token ?? string.Empty,
            HubUrl = config.HubUrl ?? string.Empty,
            HubUrlIpFallback = config.HubUrlIpFallback ?? string.Empty,
            HubUrlIpFallbackEnabled = config.HubUrlIpFallbackEnabled,
            Listen = config.Listen,
            EnvActiveNames = config.EnvActiveNames ?? [],
            EnvCustom = config.EnvCustom ?? [],
            AutoUpdateEnabled = config.AutoUpdateEnabled,
            UpdateIntervalHours = config.UpdateIntervalHours > 0 ? Math.Clamp(config.UpdateIntervalHours, 1, 720) : 24,
            AutoRestartEnabled = config.AutoRestartEnabled,
            AutoRestartIntervalHours = config.AutoRestartIntervalHours > 0 ? config.AutoRestartIntervalHours : 24,
            DebugLogging = config.DebugLogging,
            GitHubTokenEncrypted = config.GitHubTokenEncrypted ?? string.Empty,
            StartHidden = config.StartHidden,
            LastAppliedFingerprint = config.LastAppliedFingerprint ?? string.Empty,
            LastAppliedAt = config.LastAppliedAt ?? string.Empty,
            LastAppliedManagerTasksFingerprint = config.LastAppliedManagerTasksFingerprint ?? string.Empty,
            ExtraFields = config.ExtraFields ?? [],
        };
    }

    private static async Task<AgentConfig> ApplyLocalOverridesAsync(AgentConfig config, CancellationToken cancellationToken)
    {
        if (!File.Exists(ManagerPaths.LocalSettingsPath))
        {
            return config;
        }

        try
        {
            var local = JsonNode.Parse(await File.ReadAllTextAsync(ManagerPaths.LocalSettingsPath, cancellationToken)) as JsonObject;
            var debug = local?["debug_logging"]?.GetValue<bool>() ?? config.DebugLogging;
            return new AgentConfig
            {
                Key = config.Key,
                Token = config.Token,
                HubUrl = config.HubUrl,
                HubUrlIpFallback = config.HubUrlIpFallback,
                HubUrlIpFallbackEnabled = config.HubUrlIpFallbackEnabled,
                Listen = config.Listen,
                EnvActiveNames = config.EnvActiveNames,
                EnvCustom = config.EnvCustom,
                AutoUpdateEnabled = config.AutoUpdateEnabled,
                UpdateIntervalHours = config.UpdateIntervalHours,
                AutoRestartEnabled = config.AutoRestartEnabled,
                AutoRestartIntervalHours = config.AutoRestartIntervalHours,
                DebugLogging = debug,
                GitHubTokenEncrypted = config.GitHubTokenEncrypted,
                StartHidden = config.StartHidden,
                LastAppliedFingerprint = config.LastAppliedFingerprint,
                LastAppliedAt = config.LastAppliedAt,
                LastAppliedManagerTasksFingerprint = config.LastAppliedManagerTasksFingerprint,
                ExtraFields = config.ExtraFields,
            };
        }
        catch
        {
            return config;
        }
    }

    private static string EnvNameToConfigKey(string envName)
    {
        return envName.Trim().ToLowerInvariant() switch
        {
            "data_dir" => "data_dir",
            "docker_host" => "docker_host",
            "exclude_containers" => "exclude_containers",
            "exclude_smart" => "exclude_smart",
            "extra_filesystems" => "extra_filesystems",
            "filesystem" => "filesystem",
            "intel_gpu_device" => "intel_gpu_device",
            "key_file" => "key_file",
            "token_file" => "token_file",
            "lhm" => "lhm",
            "log_level" => "log_level",
            "mem_calc" => "mem_calc",
            "network" => "network",
            "nics" => "nics",
            "sensors" => "sensors",
            "sensors_timeout" => "sensors_timeout",
            "primary_sensor" => "primary_sensor",
            "sys_sensors" => "sys_sensors",
            "service_patterns" => "service_patterns",
            "smart_devices" => "smart_devices",
            "system_name" => "system_name",
            "skip_gpu" => "skip_gpu",
            "gpu_collector" => "gpu_collector",
            "disable_ssh" => "disable_ssh",
            "nvml" => "nvml",
            "smart_interval" => "smart_interval",
            "disk_usage_cache" => "disk_usage_cache",
            "skip_systemd" => "skip_systemd",
            _ => envName.Trim().ToLowerInvariant(),
        };
    }
}
