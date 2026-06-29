using System.Diagnostics;

namespace BeszelAgentManager.WinUI.Services;

internal sealed record HubStatus(bool IsConfigured, bool IsReachable, int? LatencyMilliseconds, string Url);

internal sealed class HubStatusService
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    public async Task<HubStatus> CheckAsync(string? configuredUrl, CancellationToken cancellationToken = default)
    {
        var url = NormalizeUrl(configuredUrl);
        if (string.IsNullOrEmpty(url))
        {
            return new HubStatus(false, false, null, string.Empty);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            var stopwatch = Stopwatch.StartNew();
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            stopwatch.Stop();
            return new HubStatus(true, true, (int)stopwatch.ElapsedMilliseconds, url);
        }
        catch
        {
            return new HubStatus(true, false, null, url);
        }
    }

    private static string NormalizeUrl(string? value)
    {
        var url = value?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"https://{url}";
    }
}
