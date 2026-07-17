namespace BeszelAgentManager.Core;

public readonly record struct WebSocketLogSummary(int ConsecutiveFailures, bool Connected);

public static class WebSocketBackoffPolicy
{
    public const int FailureThreshold = 12;

    private static readonly int[] RetryMinutes = [1, 2, 5, 10, 30, 60];

    public static WebSocketLogSummary Analyze(string text, int existingFailures = 0)
    {
        var failures = Math.Max(0, existingFailures);
        var connected = false;

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("WebSocket connected", StringComparison.OrdinalIgnoreCase))
            {
                failures = 0;
                connected = true;
            }
            else if (line.Contains("WebSocket connection failed", StringComparison.OrdinalIgnoreCase))
            {
                failures++;
                connected = false;
            }
        }

        return new(failures, connected);
    }

    public static TimeSpan RetryDelay(int failedRetryCount)
    {
        var index = Math.Clamp(failedRetryCount, 0, RetryMinutes.Length - 1);
        return TimeSpan.FromMinutes(RetryMinutes[index]);
    }
}
