using System.Text.Json;
using System.Text.Json.Nodes;
using BeszelAgentManager.Core;

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

    public async Task<LegacyFileSettingsMigrationResult> MigrateLegacyFileSettingsAsync(
        AgentConfig config,
        CancellationToken cancellationToken = default)
    {
        if (config.LegacyFileSettings.Count == 0)
        {
            return LegacyFileSettingsMigrationResult.Empty;
        }

        var migrated = new List<string>();
        var failed = new List<string>();
        var configurationChanged = false;

        await MigrateTextFileAsync("TOKEN_FILE", "token_file", value => config.Token = value, () => config.Token);
        await MigrateTextFileAsync("KEY_FILE", "key_file", value => config.Key = value, () => config.Key);

        var dataDirectory = GetLegacyPath(config, "data_dir");
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            try
            {
                var fingerprint = await ReadSmallTextFileAsync(
                    Path.Combine(RequireAbsoluteLocalPath(dataDirectory), "fingerprint"),
                    1024,
                    cancellationToken);
                if (!System.Text.RegularExpressions.Regex.IsMatch(
                        fingerprint,
                        @"\A[0-9a-fA-F]{48}\z",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                {
                    throw new InvalidDataException("The legacy fingerprint file is invalid.");
                }

                var importResult = await App.Broker.ImportAgentFingerprintAsync(fingerprint);
                if (importResult != 0)
                {
                    throw new InvalidOperationException($"The background service returned code {importResult}.");
                }

                config.ExtraFields.Remove("data_dir");
                migrated.Add("DATA_DIR");
                configurationChanged = true;
            }
            catch (Exception ex)
            {
                App.Logger.Warning($"Legacy DATA_DIR migration needs attention: {ex.Message}");
                failed.Add("DATA_DIR");
            }
        }

        config.LegacyFileSettings = failed.ToList();
        if (configurationChanged)
        {
            await SaveAsync(config, cancellationToken);
            if (File.Exists(ManagerPaths.AgentExePath))
            {
                var applyResult = await App.Broker.ApplyConfigurationAsync();
                if (applyResult == 0)
                {
                    config.LastAppliedFingerprint = config.ApplyFingerprint();
                    config.LastAppliedAt = DateTimeOffset.Now.ToString("O");
                    await SaveAsync(config, cancellationToken);
                }
                else
                {
                    failed.Add("APPLY_SETTINGS");
                }
            }
        }

        return new LegacyFileSettingsMigrationResult(migrated, failed);

        async Task MigrateTextFileAsync(
            string displayName,
            string configName,
            Action<string> assign,
            Func<string> currentValue)
        {
            var path = GetLegacyPath(config, configName);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(currentValue()))
            {
                config.ExtraFields.Remove(configName);
                migrated.Add(displayName);
                configurationChanged = true;
                return;
            }

            try
            {
                var value = await ReadSmallTextFileAsync(
                    RequireAbsoluteLocalPath(path),
                    16 * 1024,
                    cancellationToken);
                if (value.Length == 0 || value.IndexOfAny(['\0', '\r', '\n']) >= 0)
                {
                    throw new InvalidDataException("The legacy credential file contains an unsupported value.");
                }

                assign(value);
                config.ExtraFields.Remove(configName);
                migrated.Add(displayName);
                configurationChanged = true;
            }
            catch (Exception ex)
            {
                App.Logger.Warning($"Legacy {displayName} migration needs attention: {ex.Message}");
                failed.Add(displayName);
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
        var extraFields = new Dictionary<string, JsonElement>(config.ExtraFields ?? [], StringComparer.OrdinalIgnoreCase);
        var legacyFileSettings = new[]
            {
                (ConfigName: "data_dir", DisplayName: "DATA_DIR"),
                (ConfigName: "key_file", DisplayName: "KEY_FILE"),
                (ConfigName: "token_file", DisplayName: "TOKEN_FILE"),
            }
            .Where(item => !string.IsNullOrWhiteSpace(GetLegacyPath(extraFields, item.ConfigName)))
            .Select(static item => item.DisplayName)
            .ToList();
        return new AgentConfig
        {
            Key = config.Key ?? string.Empty,
            Token = config.Token ?? string.Empty,
            HubUrl = config.HubUrl ?? string.Empty,
            HubUrlIpFallback = config.HubUrlIpFallback ?? string.Empty,
            HubUrlIpFallbackEnabled = config.HubUrlIpFallbackEnabled,
            Listen = config.Listen,
            EnvActiveNames = (config.EnvActiveNames ?? [])
                .Where(static name => PrivilegedConfigurationValidator.IsSupportedOptionalVariable(name.Trim()))
                .ToList(),
            EnvCustom = (config.EnvCustom ?? [])
                .Where(static item => !PrivilegedConfigurationValidator.IsDeniedCustomVariable(item.Name.Trim()))
                .ToList(),
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
            WebSocketOfflineBackoffEnabled = config.WebSocketOfflineBackoffEnabled,
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
            ExtraFields = extraFields,
            LegacyFileSettings = legacyFileSettings,
        };
    }

    private static string GetLegacyPath(AgentConfig config, string name) =>
        GetLegacyPath(config.ExtraFields, name);

    private static string GetLegacyPath(IReadOnlyDictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string RequireAbsoluteLocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The legacy path is not an absolute path on a local drive.");
        }

        return Path.GetFullPath(path);
    }

    private static async Task<string> ReadSmallTextFileAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > maximumBytes || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The legacy file is missing, too large, or a reparse point.");
        }

        return (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
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

internal sealed record LegacyFileSettingsMigrationResult(
    IReadOnlyList<string> Migrated,
    IReadOnlyList<string> Failed)
{
    public static LegacyFileSettingsMigrationResult Empty { get; } = new([], []);
}
