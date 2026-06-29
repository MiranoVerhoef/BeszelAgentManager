using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace BeszelAgentManager.WinUI.Services;

internal sealed class AgentConfig
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("hub_url")]
    public string HubUrl { get; set; } = string.Empty;

    [JsonPropertyName("hub_url_ip_fallback")]
    public string HubUrlIpFallback { get; set; } = string.Empty;

    [JsonPropertyName("hub_url_ip_fallback_enabled")]
    public bool HubUrlIpFallbackEnabled { get; set; }

    [JsonPropertyName("listen")]
    public int? Listen { get; set; }

    [JsonPropertyName("env_active_names")]
    public List<string> EnvActiveNames { get; set; } = [];

    [JsonPropertyName("env_custom")]
    public List<EnvironmentVariableEntry> EnvCustom { get; set; } = [];

    [JsonPropertyName("auto_update_enabled")]
    public bool AutoUpdateEnabled { get; set; } = true;

    [JsonPropertyName("update_interval_hours")]
    public int UpdateIntervalHours { get; set; } = 24;

    [JsonPropertyName("auto_restart_enabled")]
    public bool AutoRestartEnabled { get; set; }

    [JsonPropertyName("auto_restart_interval_hours")]
    public int AutoRestartIntervalHours { get; set; } = 24;

    [JsonPropertyName("debug_logging")]
    public bool DebugLogging { get; set; }

    [JsonPropertyName("github_token_enc")]
    public string GitHubTokenEncrypted { get; set; } = string.Empty;

    [JsonPropertyName("start_hidden")]
    public bool StartHidden { get; set; } = true;

    [JsonPropertyName("last_applied_fingerprint")]
    public string LastAppliedFingerprint { get; set; } = string.Empty;

    [JsonPropertyName("last_applied_at")]
    public string LastAppliedAt { get; set; } = string.Empty;

    [JsonPropertyName("last_applied_manager_tasks_fingerprint")]
    public string LastAppliedManagerTasksFingerprint { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];

    public string GetEnvironmentValue(string configKey)
    {
        if (ExtraFields.TryGetValue(configKey, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    public string ApplyFingerprint()
    {
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = Key,
            ["token"] = Token,
            ["hub_url"] = HubUrl,
            ["listen"] = Listen,
            ["hub_url_ip_fallback"] = HubUrlIpFallback,
            ["hub_url_ip_fallback_enabled"] = HubUrlIpFallbackEnabled,
            ["env_active_names"] = string.Join("|", EnvActiveNames.Select(static name => name.Trim()).Where(static name => name.Length > 0).Order(StringComparer.OrdinalIgnoreCase)),
            ["env_custom"] = string.Join(
                "|",
                EnvCustom
                    .Where(static item => !string.IsNullOrWhiteSpace(item.Name))
                    .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(static item => $"{item.Name.Trim()}={item.Value}")),
        };

        AddExtra("data_dir");
        AddExtra("docker_host");
        AddExtra("exclude_containers");
        AddExtra("exclude_smart");
        AddExtra("extra_filesystems");
        AddExtra("filesystem");
        AddExtra("intel_gpu_device");
        AddExtra("key_file");
        AddExtra("token_file");
        AddExtra("lhm");
        AddExtra("log_level");
        AddExtra("mem_calc");
        AddExtra("network");
        AddExtra("nics");
        AddExtra("sensors");
        AddExtra("sensors_timeout");
        AddExtra("primary_sensor");
        AddExtra("sys_sensors");
        AddExtra("service_patterns");
        AddExtra("smart_devices");
        AddExtra("system_name");
        AddExtra("skip_gpu");
        AddExtra("gpu_collector");
        AddExtra("disable_ssh");
        AddExtra("nvml");
        AddExtra("smart_interval");
        AddExtra("disk_usage_cache");
        AddExtra("skip_systemd");

        var builder = new StringBuilder();
        foreach (var item in values)
        {
            builder
                .Append(item.Key)
                .Append('=')
                .Append(item.Value?.ToString() ?? string.Empty)
                .Append('\n');
        }

        var payload = builder.ToString();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();

        void AddExtra(string key)
        {
            values[key] = GetEnvironmentValue(key);
        }
    }

    public string ManagerTasksFingerprint()
    {
        var payload = $"auto_update_enabled={AutoUpdateEnabled}\nupdate_interval_hours={UpdateIntervalHours}\n";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

internal sealed class EnvironmentVariableEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}
