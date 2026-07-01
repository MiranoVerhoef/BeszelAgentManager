using BeszelAgentManager.Core;
using Xunit;

namespace BeszelAgentManager.Core.Tests;

public sealed class DnsFailoverCoordinatorTests
{
    private static readonly DnsFailoverConfiguration Enabled = new(
        true,
        "https://primary.example:8090",
        "http://192.0.2.10:8090");

    [Fact]
    public async Task PrimaryDnsFailureActivatesFallback()
    {
        var fixture = new Fixture(false);
        var state = new DnsFailoverState();

        await fixture.Coordinator.CheckAsync(Enabled, state, TestContext.Current.CancellationToken);

        Assert.True(state.Active);
        Assert.Equal((Enabled.FallbackUrl, "fallback"), Assert.Single(fixture.Agent.Applied));
    }

    [Fact]
    public async Task ContinuedFailureDoesNotReapplyFallback()
    {
        var fixture = new Fixture(false);
        var state = new DnsFailoverState { Active = true, SuccessStreak = 3 };

        await fixture.Coordinator.CheckAsync(Enabled, state, TestContext.Current.CancellationToken);
        await fixture.Coordinator.CheckAsync(Enabled, state, TestContext.Current.CancellationToken);

        Assert.True(state.Active);
        Assert.Equal(0, state.SuccessStreak);
        Assert.Empty(fixture.Agent.Applied);
    }

    [Fact]
    public async Task FiveSuccessfulChecksRestorePrimary()
    {
        var fixture = new Fixture(true);
        var state = new DnsFailoverState { Active = true };

        for (var check = 0; check < 5; check++)
        {
            await fixture.Coordinator.CheckAsync(Enabled, state, TestContext.Current.CancellationToken);
        }

        Assert.False(state.Active);
        Assert.Equal(0, state.SuccessStreak);
        Assert.Equal((Enabled.PrimaryUrl, "primary"), Assert.Single(fixture.Agent.Applied));
    }

    [Fact]
    public async Task DisablingFallbackRestoresPrimary()
    {
        var fixture = new Fixture(false);
        var state = new DnsFailoverState { Active = true };

        await fixture.Coordinator.CheckAsync(Enabled with { Enabled = false }, state, TestContext.Current.CancellationToken);

        Assert.False(state.Active);
        Assert.Equal((Enabled.PrimaryUrl, "primary"), Assert.Single(fixture.Agent.Applied));
    }

    [Fact]
    public async Task StateSurvivesStoreRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bam-failover-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonDnsFailoverStateStore(path);
            await store.SaveAsync(Enabled, new() { Active = true, SuccessStreak = 4 }, DateTimeOffset.Parse("2026-06-30T01:00:00+02:00"));

            var reloaded = new JsonDnsFailoverStateStore(path).Load();

            Assert.True(reloaded.Active);
            Assert.Equal(4, reloaded.SuccessStreak);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FailedFallbackApplicationKeepsPrimaryState()
    {
        var agent = new FakeAgentConfiguration { ApplyResult = false };
        var coordinator = new DnsFailoverCoordinator(
            new FakeDnsResolver(false), agent, new MemoryStateStore(), new FixedClock());
        var state = new DnsFailoverState();

        await coordinator.CheckAsync(Enabled, state, TestContext.Current.CancellationToken);

        Assert.False(state.Active);
        Assert.Single(agent.Applied);
    }

    [Fact]
    public async Task FailedPrimaryRestoreRetainsFallbackForRetry()
    {
        var agent = new FakeAgentConfiguration { ApplyResult = false };
        var coordinator = new DnsFailoverCoordinator(
            new FakeDnsResolver(true), agent, new MemoryStateStore(), new FixedClock());
        var state = new DnsFailoverState { Active = true, SuccessStreak = 4 };

        await coordinator.CheckAsync(Enabled, state, TestContext.Current.CancellationToken);

        Assert.True(state.Active);
        Assert.Equal(5, state.SuccessStreak);
        Assert.Single(agent.Applied);
    }

    [Fact]
    public void CorruptStateFileLoadsSafeDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bam-failover-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "not-json");

            var state = new JsonDnsFailoverStateStore(path).Load();

            Assert.False(state.Active);
            Assert.Equal(0, state.SuccessStreak);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class Fixture
    {
        public Fixture(bool dnsResult)
        {
            Agent = new FakeAgentConfiguration();
            Coordinator = new(
                new FakeDnsResolver(dnsResult),
                Agent,
                new MemoryStateStore(),
                new FixedClock());
        }

        public FakeAgentConfiguration Agent { get; }
        public DnsFailoverCoordinator Coordinator { get; }
    }

    private sealed class FakeDnsResolver(bool result) : IDnsResolver
    {
        public Task<bool> CanResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class FakeAgentConfiguration : IAgentHubUrlConfiguration
    {
        public List<(string Url, string Destination)> Applied { get; } = [];
        public bool ApplyResult { get; init; } = true;

        public Task<bool> ApplyAsync(string hubUrl, string destination)
        {
            Applied.Add((hubUrl, destination));
            return Task.FromResult(ApplyResult);
        }
    }

    private sealed class MemoryStateStore : IDnsFailoverStateStore
    {
        public DnsFailoverState Load() => new();
        public Task SaveAsync(DnsFailoverConfiguration configuration, DnsFailoverState state, DateTimeOffset timestamp) => Task.CompletedTask;
    }

    private sealed class FixedClock : IFailoverClock
    {
        public DateTimeOffset Now => DateTimeOffset.Parse("2026-06-30T01:00:00+02:00");
    }
}
