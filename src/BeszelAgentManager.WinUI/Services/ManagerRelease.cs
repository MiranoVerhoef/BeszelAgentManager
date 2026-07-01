namespace BeszelAgentManager.WinUI.Services;

internal sealed record ManagerRelease(
    string Version,
    string Tag,
    string? Body,
    string DownloadUrl,
    bool IsPrerelease,
    DateTimeOffset? PublishedAt);
