using BeszelAgentManager.Core;
using Xunit;

namespace BeszelAgentManager.Core.Tests;

public sealed class WebSocketBackoffPolicyTests
{
    [Fact]
    public void FailuresAccumulateAcrossLogChunks()
    {
        var first = WebSocketBackoffPolicy.Analyze("WARN WebSocket connection failed\nWARN WebSocket connection failed");
        var second = WebSocketBackoffPolicy.Analyze("WARN WebSocket connection failed", first.ConsecutiveFailures);

        Assert.Equal(3, second.ConsecutiveFailures);
        Assert.False(second.Connected);
    }

    [Fact]
    public void SuccessfulConnectionResetsFailures()
    {
        var result = WebSocketBackoffPolicy.Analyze(
            "WARN WebSocket connection failed\nINFO WebSocket connected host=home.example",
            11);

        Assert.Equal(0, result.ConsecutiveFailures);
        Assert.True(result.Connected);
    }

    [Fact]
    public void LaterFailureOverridesEarlierConnection()
    {
        var result = WebSocketBackoffPolicy.Analyze(
            "INFO WebSocket connected host=home.example\nWARN WebSocket connection failed");

        Assert.Equal(1, result.ConsecutiveFailures);
        Assert.False(result.Connected);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 5)]
    [InlineData(3, 10)]
    [InlineData(4, 30)]
    [InlineData(5, 60)]
    [InlineData(99, 60)]
    public void RetryDelayGrowsAndCaps(int failedRetryCount, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), WebSocketBackoffPolicy.RetryDelay(failedRetryCount));
    }
}
