using System.Security.Cryptography;
using System.Text;
using BeszelAgentManager.Core;
using Xunit;

namespace BeszelAgentManager.Core.Tests;

public sealed class ReleaseChecksumVerifierTests
{
    [Fact]
    public void VerifyFileAcceptsMatchingOfficialFormat()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "verified agent payload");
            var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("verified agent payload")));
            var checksums = $"{expected.ToLowerInvariant()}  beszel-agent_windows_amd64.zip\n";

            Assert.True(ReleaseChecksumVerifier.VerifyFile(
                path,
                checksums,
                "beszel-agent_windows_amd64.zip"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VerifyFileRejectsMismatch()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "modified payload");
            var checksums = $"{new string('0', 64)}  beszel-agent_windows_amd64.zip\n";

            Assert.False(ReleaseChecksumVerifier.VerifyFile(
                path,
                checksums,
                "beszel-agent_windows_amd64.zip"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VerifyFileRejectsMissingAssetEntry()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "payload");
            var checksums = $"{new string('0', 64)}  another-file.zip\n";

            Assert.False(ReleaseChecksumVerifier.VerifyFile(
                path,
                checksums,
                "beszel-agent_windows_amd64.zip"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("../beszel-agent_windows_amd64.zip")]
    [InlineData("folder/beszel-agent_windows_amd64.zip")]
    public void ChecksumLookupRejectsNonFileNames(string fileName)
    {
        Assert.False(ReleaseChecksumVerifier.TryGetExpectedSha256(
            $"{new string('0', 64)}  {fileName}\n",
            fileName,
            out _));
    }
}
