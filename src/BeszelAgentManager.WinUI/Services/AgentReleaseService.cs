using System.Net.Http.Headers;
using System.Text.Json;

namespace BeszelAgentManager.WinUI.Services;

internal sealed class AgentReleaseService
{
    private const string AgentRepo = "henrygd/beszel";
    private const string AgentAssetName = "beszel-agent_windows_amd64.zip";
    private readonly HttpClient _httpClient = new();
    private readonly GitHubTokenService _gitHubTokenService = new();

    public async Task<IReadOnlyList<AgentRelease>> FetchStableReleasesAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{AgentRepo}/releases?per_page=50");
        await ApplyGitHubHeadersAsync(request, cancellationToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        LogGitHubAuthSuccess(response);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return document.RootElement
            .EnumerateArray()
            .Select(ParseRelease)
            .Where(static release => release is not null)
            .Select(static release => release!)
            .OrderByDescending(static release => ToVersionKey(release.Version))
            .ToList();
    }

    private static AgentRelease? ParseRelease(JsonElement element)
    {
        if ((element.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            || (element.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()))
        {
            return null;
        }

        var tag = GetString(element, "tag_name");
        var version = VersionComparer.Normalize(tag);
        if (string.IsNullOrWhiteSpace(version)
            || !element.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var hasWindowsAsset = assets.EnumerateArray().Any(asset =>
            string.Equals(GetString(asset, "name"), AgentAssetName, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(GetString(asset, "browser_download_url")));

        return hasWindowsAsset
            ? new AgentRelease(version, tag, GetString(element, "body"))
            : null;
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

    private async Task ApplyGitHubHeadersAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.ProjectName, AppInfo.Version));
        var token = await _gitHubTokenService.GetEffectiveTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            App.Logger.Info("GitHub Auth: using token for agent release lookup");
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
        App.Logger.Info($"GitHub Auth: token used successfully for agent release lookup (rate_limit_remaining={remaining}/{limit})");
    }
}

internal sealed record AgentRelease(string Version, string Tag, string Body);
