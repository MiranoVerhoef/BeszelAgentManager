namespace BeszelAgentManager.Core;

public static class SecureVersionPolicy
{
    public static bool IsSameOrNewer(string candidate, string installed)
    {
        if (!TryParse(candidate, out var candidateVersion)
            || !TryParse(installed, out var installedVersion))
        {
            return false;
        }

        var coreComparison = candidateVersion.Core.CompareTo(installedVersion.Core);
        if (coreComparison != 0)
        {
            return coreComparison > 0;
        }
        if (candidateVersion.Prerelease is null)
        {
            return true;
        }
        if (installedVersion.Prerelease is null)
        {
            return false;
        }
        return ComparePrerelease(candidateVersion.Prerelease, installedVersion.Prerelease) >= 0;
    }

    private static bool TryParse(string value, out ParsedVersion version)
    {
        version = default;
        var normalized = value.Trim().TrimStart('v', 'V').Split('+')[0];
        var separator = normalized.IndexOf('-');
        var coreText = separator >= 0 ? normalized[..separator] : normalized;
        var prerelease = separator >= 0 ? normalized[(separator + 1)..] : null;
        if (!Version.TryParse(coreText, out var core)
            || (prerelease is not null && string.IsNullOrWhiteSpace(prerelease)))
        {
            return false;
        }
        version = new ParsedVersion(
            new Version(core.Major, core.Minor, Math.Max(0, core.Build), Math.Max(0, core.Revision)),
            prerelease);
        return true;
    }

    private static int ComparePrerelease(string candidate, string installed)
    {
        var candidateParts = candidate.Split('.');
        var installedParts = installed.Split('.');
        for (var index = 0; index < Math.Max(candidateParts.Length, installedParts.Length); index++)
        {
            if (index >= candidateParts.Length)
            {
                return -1;
            }
            if (index >= installedParts.Length)
            {
                return 1;
            }

            var candidateNumeric = int.TryParse(candidateParts[index], out var candidateNumber);
            var installedNumeric = int.TryParse(installedParts[index], out var installedNumber);
            var comparison = candidateNumeric && installedNumeric
                ? candidateNumber.CompareTo(installedNumber)
                : candidateNumeric
                    ? -1
                    : installedNumeric
                        ? 1
                        : string.Compare(candidateParts[index], installedParts[index], StringComparison.OrdinalIgnoreCase);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return 0;
    }

    private readonly record struct ParsedVersion(Version Core, string? Prerelease);
}
