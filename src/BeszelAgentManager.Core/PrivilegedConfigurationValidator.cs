using System.Text.Json;
using System.Text.RegularExpressions;

namespace BeszelAgentManager.Core;

public static class PrivilegedConfigurationValidator
{
    private static readonly HashSet<string> ReservedAgentVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "KEY",
        "TOKEN",
        "HUB_URL",
        "LISTEN",
    };

    private static readonly HashSet<string> SupportedOptionalVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALL_PROXY",
        "DISABLE_SSH",
        "DISK_USAGE_CACHE",
        "DOCKER_HOST",
        "DOCKER_TIMEOUT",
        "EXCLUDE_CONTAINERS",
        "EXCLUDE_SMART",
        "EXIT_ON_DNS_ERROR",
        "EXTRA_FILESYSTEMS",
        "FILESYSTEM",
        "GPU_COLLECTOR",
        "INTEL_GPU_DEVICE",
        "LHM",
        "LOG_LEVEL",
        "MEM_CALC",
        "NETWORK",
        "NICS",
        "NVML",
        "PRIMARY_SENSOR",
        "SENSORS",
        "SENSORS_TIMEOUT",
        "SERVICE_PATTERNS",
        "SKIP_GPU",
        "SKIP_SYSTEMD",
        "SMART_DEVICES",
        "SMART_DEVICES_SEPARATOR",
        "SMART_INTERVAL",
        "SYS_SENSORS",
        "SYSTEM_NAME",
    };

    public static ConfigurationValidationResult Validate(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
        {
            return ConfigurationValidationResult.Failed("Configuration must be a JSON object.");
        }

        foreach (var propertyName in new[] { "key", "token", "hub_url", "hub_url_ip_fallback" })
        {
            if (config.TryGetProperty(propertyName, out var value)
                && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                return ConfigurationValidationResult.Failed($"{propertyName} must be text.");
            }

            if (value.ValueKind == JsonValueKind.String
                && !IsSafeValue(value.GetString(), 16 * 1024))
            {
                return ConfigurationValidationResult.Failed($"{propertyName} contains unsupported characters or is too long.");
            }
        }

        foreach (var propertyName in new[] { "hub_url", "hub_url_ip_fallback" })
        {
            var value = config.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()?.Trim()
                : null;
            if (!string.IsNullOrEmpty(value) && !IsSupportedHubUrl(value))
            {
                return ConfigurationValidationResult.Failed($"{propertyName} must be an absolute HTTP, HTTPS, WS, or WSS URL.");
            }
        }

        if (config.TryGetProperty("env_active_names", out var activeNames))
        {
            if (activeNames.ValueKind != JsonValueKind.Array || activeNames.GetArrayLength() > 64)
            {
                return ConfigurationValidationResult.Failed("env_active_names must contain at most 64 entries.");
            }

            foreach (var item in activeNames.EnumerateArray())
            {
                var name = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
                {
                    return ConfigurationValidationResult.Failed("An active environment-variable name is invalid.");
                }
                if (!IsSupportedOptionalVariable(name))
                {
                    return ConfigurationValidationResult.Failed($"{name} is not an allowlisted Beszel Agent variable.");
                }
            }
        }

        if (config.TryGetProperty("env_custom", out var customVariables))
        {
            if (customVariables.ValueKind != JsonValueKind.Array || customVariables.GetArrayLength() > 64)
            {
                return ConfigurationValidationResult.Failed("env_custom must contain at most 64 entries.");
            }

            foreach (var item in customVariables.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("name", out var nameProperty)
                    || !item.TryGetProperty("value", out var valueProperty)
                    || nameProperty.ValueKind != JsonValueKind.String)
                {
                    return ConfigurationValidationResult.Failed("A custom environment-variable entry is invalid.");
                }

                var name = nameProperty.GetString()?.Trim() ?? string.Empty;
                var value = valueProperty.ValueKind == JsonValueKind.String
                    ? valueProperty.GetString()
                    : valueProperty.ToString();
                if (!Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z", RegexOptions.CultureInvariant)
                    || !IsSafeValue(value, 16 * 1024))
                {
                    return ConfigurationValidationResult.Failed("A custom environment variable contains an invalid name or value.");
                }
                if (IsDeniedCustomVariable(name))
                {
                    return ConfigurationValidationResult.Failed($"Custom environment variable {name} is reserved or unsafe for a LocalSystem service.");
                }
            }
        }

        return ConfigurationValidationResult.Successful;
    }

    public static bool IsSupportedOptionalVariable(string name) =>
        SupportedOptionalVariables.Contains(name);

    public static bool IsUnsafeAppliedVariable(string name) =>
        !ReservedAgentVariables.Contains(name) && !SupportedOptionalVariables.Contains(name);

    public static bool IsDeniedCustomVariable(string name) =>
        ReservedAgentVariables.Contains(name) || !SupportedOptionalVariables.Contains(name);

    private static bool IsSafeValue(string? value, int maximumLength) =>
        value is not null
        && value.Length <= maximumLength
        && value.IndexOfAny(['\0', '\r', '\n']) < 0;

    public static bool IsSupportedHubUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && !string.IsNullOrWhiteSpace(uri.Host)
        && uri.Scheme is "http" or "https" or "ws" or "wss"
        && string.IsNullOrEmpty(uri.UserInfo);
}

public readonly record struct ConfigurationValidationResult(bool Success, string Message)
{
    public static ConfigurationValidationResult Successful => new(true, string.Empty);
    public static ConfigurationValidationResult Failed(string message) => new(false, message);
}
