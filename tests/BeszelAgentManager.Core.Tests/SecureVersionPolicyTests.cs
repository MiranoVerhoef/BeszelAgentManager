using BeszelAgentManager.Core;
using Xunit;

namespace BeszelAgentManager.Core.Tests;

public sealed class SecureVersionPolicyTests
{
    [Theory]
    [InlineData("4.0.10", "4.0.9", true)]
    [InlineData("v4.0.10-rc1", "4.0.10", false)]
    [InlineData("4.0.10", "4.0.10-rc1", true)]
    [InlineData("4.0.10-rc2", "4.0.10-rc1", true)]
    [InlineData("4.0.10-rc1", "4.0.10-rc2", false)]
    [InlineData("4.0.10", "4.0.10.0", true)]
    [InlineData("4.0.9", "4.0.10", false)]
    [InlineData("0.18.8", "0.18.8", true)]
    [InlineData("invalid", "4.0.9", false)]
    public void ComparesCoreReleaseVersion(string candidate, string installed, bool expected)
    {
        Assert.Equal(expected, SecureVersionPolicy.IsSameOrNewer(candidate, installed));
    }
}
