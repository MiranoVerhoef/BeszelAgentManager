namespace BeszelAgentManager.Core;

public static class RetryStrategyPolicy
{
    public static bool HasConflict(bool webSocketOfflineBackoffEnabled, string? exitOnDnsError)
    {
        if (!webSocketOfflineBackoffEnabled)
        {
            return false;
        }

        var value = exitOnDnsError?.Trim();
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.Ordinal);
    }
}
