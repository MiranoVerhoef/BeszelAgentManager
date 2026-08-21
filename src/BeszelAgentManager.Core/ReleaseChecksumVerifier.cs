using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace BeszelAgentManager.Core;

public static class ReleaseChecksumVerifier
{
    public static bool VerifyFile(string filePath, string checksumText, string expectedFileName)
    {
        if (!TryGetExpectedSha256(checksumText, expectedFileName, out var expected))
        {
            return false;
        }

        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetExpectedSha256(
        string checksumText,
        string expectedFileName,
        out string expectedSha256)
    {
        expectedSha256 = string.Empty;
        if (string.IsNullOrWhiteSpace(checksumText)
            || string.IsNullOrWhiteSpace(expectedFileName)
            || Path.GetFileName(expectedFileName) != expectedFileName)
        {
            return false;
        }

        var pattern = $@"^\s*([a-fA-F0-9]{{64}})\s+\*?{Regex.Escape(expectedFileName)}\s*$";
        foreach (var line in checksumText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = Regex.Match(line, pattern, RegexOptions.CultureInvariant);
            if (match.Success)
            {
                expectedSha256 = match.Groups[1].Value;
                return true;
            }
        }

        return false;
    }
}
