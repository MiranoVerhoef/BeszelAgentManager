using System.Text.RegularExpressions;

namespace BeszelAgentManager.WinUI.Services;

internal static partial class VersionComparer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[1..].Trim();
        }

        var match = SemVerRegex().Match(trimmed);
        return match.Success ? match.Value : trimmed;
    }

    public static bool IsUpdateAvailable(string currentVersion, string latestVersion)
    {
        return Compare(latestVersion, currentVersion) > 0;
    }

    public static bool IsSameOrOlder(string currentVersion, string latestVersion)
    {
        return !IsUpdateAvailable(currentVersion, latestVersion);
    }

    public static int Compare(string left, string right)
    {
        var leftVersion = Parse(left);
        var rightVersion = Parse(right);
        var coreComparison = leftVersion.Core.CompareTo(rightVersion.Core);
        if (coreComparison != 0)
        {
            return coreComparison;
        }

        if (leftVersion.Prerelease is null)
        {
            return rightVersion.Prerelease is null ? 0 : 1;
        }
        if (rightVersion.Prerelease is null)
        {
            return -1;
        }

        var leftRc = ParseRc(leftVersion.Prerelease);
        var rightRc = ParseRc(rightVersion.Prerelease);
        if (leftRc.HasValue && rightRc.HasValue)
        {
            return leftRc.Value.CompareTo(rightRc.Value);
        }

        return StringComparer.OrdinalIgnoreCase.Compare(leftVersion.Prerelease, rightVersion.Prerelease);
    }

    public static int CompareCore(string left, string right) => Parse(left).Core.CompareTo(Parse(right).Core);

    public static bool HasSameCore(string left, string right) => CompareCore(left, right) == 0;

    private static (Version Core, string? Prerelease) Parse(string value)
    {
        var version = Normalize(value);
        var versionParts = version.Split('-', 2, StringSplitOptions.TrimEntries);
        var parts = versionParts[0].Split('.', StringSplitOptions.RemoveEmptyEntries);
        return (
            new Version(ParsePart(parts, 0), ParsePart(parts, 1), ParsePart(parts, 2)),
            versionParts.Length == 2 && !string.IsNullOrWhiteSpace(versionParts[1]) ? versionParts[1] : null);
    }

    private static int? ParseRc(string prerelease) =>
        prerelease.StartsWith("rc", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(prerelease.AsSpan(2), out var value)
            ? value
            : null;

    private static int ParsePart(string[] parts, int index)
    {
        if (index >= parts.Length)
        {
            return 0;
        }

        return int.TryParse(parts[index], out var parsed) ? parsed : 0;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?")]
    private static partial Regex SemVerRegex();
}
