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

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, ManagerPaths.ConfigPath, overwrite: true);
                break;
            }
            catch (Exception ex) when (attempt < 6 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 100), cancellationToken);
            }
        }
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
            AutoRestartIntervalValue = config.AutoRestartIntervalValue > 0
                ? config.AutoRestartIntervalValue
                : config.AutoRestartIntervalHours > 0 ? config.AutoRestartIntervalHours : 24,
            AutoRestartIntervalUnit = string.Equals(config.AutoRestartIntervalUnit, "minutes", StringComparison.OrdinalIgnoreCase)
                ? "minutes"
                : "hours",
            DebugLogging = config.DebugLogging,
            GitHubTokenEncrypted = config.GitHubTokenEncrypted ?? string.Empty,
            StartHidden = config.StartHidden,
            ManagerUpdateNotifyEnabled = config.ManagerUpdateNotifyEnabled,
            ManagerUpdateCheckIntervalHours = Math.Clamp(config.ManagerUpdateCheckIntervalHours, 1, 168),
            ManagerUpdateSkipVersion = config.ManagerUpdateSkipVersion ?? string.Empty,
            ManagerUpdateTrayBadgeEnabled = config.ManagerUpdateTrayBadgeEnabled,
            ManagerUpdateIncludePrereleases = config.ManagerUpdateIncludePrereleases,
            ManagerUpdateLastCheckAt = config.ManagerUpdateLastCheckAt ?? string.Empty,
            ManagerUpdateLastNotifiedVersion = config.ManagerUpdateLastNotifiedVersion ?? string.Empty,
            DefenderExclusionEnabled = config.DefenderExclusionEnabled,
            DefenderExclusionPrompted = config.DefenderExclusionPrompted,
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
            config.DebugLogging = local?["debug_logging"]?.GetValue<bool>() ?? config.DebugLogging;
            return config;
        }
        catch
        {
            return config;
        }
    }

    private static string EnvNameToConfigKey(string envName)
    {
        return envName.Trim().ToLowerInvariant();
    }
}
