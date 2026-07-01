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
        var latest = ToTuple(latestVersion);
        var current = ToTuple(currentVersion);

        if (latest.Item1 != current.Item1)
        {
            return latest.Item1 > current.Item1;
        }

        if (latest.Item2 != current.Item2)
        {
            return latest.Item2 > current.Item2;
        }

        return latest.Item3 > current.Item3;
    }

    public static bool IsSameOrOlder(string currentVersion, string latestVersion)
    {
        return !IsUpdateAvailable(currentVersion, latestVersion);
    }

    private static Tuple<int, int, int> ToTuple(string value)
    {
        var version = Normalize(value);
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return Tuple.Create(ParsePart(parts, 0), ParsePart(parts, 1), ParsePart(parts, 2));
    }

    private static int ParsePart(string[] parts, int index)
    {
        if (index >= parts.Length)
        {
            return 0;
        }

        return int.TryParse(parts[index], out var parsed) ? parsed : 0;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex SemVerRegex();
}
