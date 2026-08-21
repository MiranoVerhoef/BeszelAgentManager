using BeszelAgentManager.Core;
using Xunit;

namespace BeszelAgentManager.Core.Tests;

public sealed class RetryStrategyPolicyTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("1")]
    public void RejectsTwoEnabledRetryStrategies(string exitOnDnsError)
    {
        Assert.True(RetryStrategyPolicy.HasConflict(true, exitOnDnsError));
    }

    [Theory]
    [InlineData(false, "true")]
    [InlineData(true, "false")]
    [InlineData(true, "0")]
    [InlineData(true, "")]
    public void AcceptsSingleRetryStrategy(bool backoffEnabled, string exitOnDnsError)
    {
        Assert.False(RetryStrategyPolicy.HasConflict(backoffEnabled, exitOnDnsError));
    }
}
