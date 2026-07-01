using System.Net.Http.Headers;
using System.Text.Json;

namespace BeszelAgentManager.WinUI.Services;

internal sealed class ManagerUpdateService
{
    private readonly HttpClient _httpClient = new();
    private readonly GitHubTokenService _gitHubTokenService = new();

    public async Task<ManagerRelease?> FetchLatestReleaseAsync(bool includePrereleases, CancellationToken cancellationToken = default)
    {
        var releases = await FetchReleasesAsync(includePrereleases, cancellationToken);
        return releases.FirstOrDefault();
    }

    public async Task<IReadOnlyList<ManagerRelease>> FetchReleasesAsync(bool includePrereleases, CancellationToken cancellationToken = default)
    {
        var url = $"https://api.github.com/repos/{AppInfo.ManagerRepo}/releases?per_page=50";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyGitHubHeadersAsync(request, cancellationToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        LogGitHubAuthSuccess(response);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var releases = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(ParseRelease)
            : new[] { ParseRelease(document.RootElement) };

        return releases
            .Where(static r => r is not null)
            .Select(static r => r!)
            .Where(r => includePrereleases || !r.IsPrerelease)
            .OrderByDescending(r => ToVersionKey(r.Version))
            .ToList();
    }

    private static ManagerRelease? ParseRelease(JsonElement element)
    {
        if (element.TryGetProperty("draft", out var draft) && draft.GetBoolean())
        {
            return null;
        }

        var tag = GetString(element, "tag_name");
        var version = VersionComparer.Normalize(tag);
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var downloadUrl = FindInstallerAsset(element);
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            return null;
        }

        DateTimeOffset? publishedAt = null;
        if (DateTimeOffset.TryParse(GetString(element, "published_at"), out var parsedPublishedAt))
        {
            publishedAt = parsedPublishedAt;
        }

        return new ManagerRelease(
            version,
            tag,
            GetString(element, "body"),
            downloadUrl,
            element.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean(),
            publishedAt);
    }

    private async Task ApplyGitHubHeadersAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.ProjectName, AppInfo.Version));
        var token = await _gitHubTokenService.GetEffectiveTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            App.Logger.Info("GitHub Auth: using token for manager release lookup");
        }
    }

    private static void LogGitHubAuthSuccess(HttpResponseMessage response)
    {
        if (response.RequestMessage?.Headers.Authorization is null)
        {
            return;
        }

        var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues)
            ? remainingValues.FirstOrDefault() ?? "?"
            : "?";
        var limit = response.Headers.TryGetValues("X-RateLimit-Limit", out var limitValues)
            ? limitValues.FirstOrDefault() ?? "?"
            : "?";
        App.Logger.Info($"GitHub Auth: token used successfully for manager release lookup (rate_limit_remaining={remaining}/{limit})");
    }

    private static string FindInstallerAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");
            if (!string.Equals(name, AppInfo.InstallerAssetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return GetString(asset, "browser_download_url");
        }

        return string.Empty;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static Tuple<int, int, int> ToVersionKey(string version)
    {
        var normalized = VersionComparer.Normalize(version);
        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return Tuple.Create(ParsePart(parts, 0), ParsePart(parts, 1), ParsePart(parts, 2));
    }

    private static int ParsePart(string[] parts, int index)
    {
        return index < parts.Length && int.TryParse(parts[index], out var parsed) ? parsed : 0;
    }
}
