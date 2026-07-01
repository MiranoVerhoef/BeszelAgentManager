using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeszelAgentManager.Core;

public sealed record DnsFailoverConfiguration(
    bool Enabled,
    string PrimaryUrl,
    string FallbackUrl);

public sealed class DnsFailoverState
{
    public bool Active { get; set; }
    public int SuccessStreak { get; set; }
}

public enum DnsFailoverEventKind
{
    FallbackActivated,
    RecoveryProgress,
    PrimaryRestored,
    DisabledPrimaryRestored,
}

public sealed record DnsFailoverEvent(DnsFailoverEventKind Kind, string Host, int SuccessStreak);

public interface IDnsResolver
{
    Task<bool> CanResolveAsync(string host, CancellationToken cancellationToken);
}

public interface IAgentHubUrlConfiguration
{
    Task<bool> ApplyAsync(string hubUrl, string destination);
}

public interface IFailoverClock
{
    DateTimeOffset Now { get; }
}

public interface IDnsFailoverStateStore
{
    DnsFailoverState Load();
    Task SaveAsync(DnsFailoverConfiguration configuration, DnsFailoverState state, DateTimeOffset timestamp);
}

public sealed class SystemFailoverClock : IFailoverClock
{
    public static SystemFailoverClock Instance { get; } = new();
    public DateTimeOffset Now => DateTimeOffset.Now;
}

public sealed class DelegateDnsResolver(Func<string, CancellationToken, Task<bool>> resolve) : IDnsResolver
{
    public Task<bool> CanResolveAsync(string host, CancellationToken cancellationToken) => resolve(host, cancellationToken);
}

public sealed class DelegateAgentHubUrlConfiguration(Func<string, string, Task<bool>> apply) : IAgentHubUrlConfiguration
{
    public Task<bool> ApplyAsync(string hubUrl, string destination) => apply(hubUrl, destination);
}

public sealed class DnsFailoverCoordinator(
    IDnsResolver dnsResolver,
    IAgentHubUrlConfiguration agentConfiguration,
    IDnsFailoverStateStore stateStore,
    IFailoverClock clock)
{
    public async Task<IReadOnlyList<DnsFailoverEvent>> CheckAsync(
        DnsFailoverConfiguration configuration,
        DnsFailoverState state,
        CancellationToken cancellationToken = default)
    {
        var events = new List<DnsFailoverEvent>();
        var primary = configuration.PrimaryUrl.Trim();
        var fallback = configuration.FallbackUrl.Trim();
        var host = ExtractHost(primary);
        var enabled = configuration.Enabled
            && primary.Length > 0
            && fallback.Length > 0
            && host.Length > 0;

        if (!enabled)
        {
            if (state.Active
                && primary.Length > 0
                && await agentConfiguration.ApplyAsync(primary, "primary"))
            {
                state.Active = false;
                state.SuccessStreak = 0;
                events.Add(new(DnsFailoverEventKind.DisabledPrimaryRestored, host, 0));
            }

            await stateStore.SaveAsync(configuration, state, clock.Now);
            return events;
        }

        var dnsAvailable = await dnsResolver.CanResolveAsync(host, cancellationToken);
        if (!state.Active)
        {
            if (!dnsAvailable
                && await agentConfiguration.ApplyAsync(fallback, "fallback"))
            {
                state.Active = true;
                state.SuccessStreak = 0;
                events.Add(new(DnsFailoverEventKind.FallbackActivated, host, 0));
            }
        }
        else if (!dnsAvailable)
        {
            state.SuccessStreak = 0;
        }
        else
        {
            state.SuccessStreak = Math.Min(state.SuccessStreak + 1, 5);
            if (state.SuccessStreak is 1 or 5)
            {
                events.Add(new(DnsFailoverEventKind.RecoveryProgress, host, state.SuccessStreak));
            }

            if (state.SuccessStreak >= 5
                && await agentConfiguration.ApplyAsync(primary, "primary"))
            {
                state.Active = false;
                state.SuccessStreak = 0;
                events.Add(new(DnsFailoverEventKind.PrimaryRestored, host, 0));
            }
        }

        await stateStore.SaveAsync(configuration, state, clock.Now);
        return events;
    }

    private static string ExtractHost(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    }
}

public sealed class JsonDnsFailoverStateStore(string path) : IDnsFailoverStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public DnsFailoverState Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new();
            }

            var persisted = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(path), JsonOptions);
            return persisted is null
                ? new()
                : new()
                {
                    Active = persisted.Active,
                    SuccessStreak = Math.Clamp(persisted.SuccessStreak, 0, 5),
                };
        }
        catch
        {
            return new();
        }
    }

    public async Task SaveAsync(
        DnsFailoverConfiguration configuration,
        DnsFailoverState state,
        DateTimeOffset timestamp)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var persisted = new PersistedState
            {
                Timestamp = timestamp.ToString("s"),
                Owner = "background-service",
                Enabled = configuration.Enabled,
                Primary = configuration.PrimaryUrl,
                Fallback = configuration.FallbackUrl,
                Active = state.Active,
                SuccessStreak = Math.Clamp(state.SuccessStreak, 0, 5),
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(persisted, JsonOptions));
        }
        catch
        {
            // State persistence must not terminate the LocalSystem monitoring loop.
        }
    }

    private sealed class PersistedState
    {
        public string Timestamp { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public string Primary { get; set; } = string.Empty;
        public string Fallback { get; set; } = string.Empty;
        public bool Active { get; set; }
        public int SuccessStreak { get; set; }
    }
}
