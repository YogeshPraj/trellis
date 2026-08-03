using System.Collections.Concurrent;

namespace Trellis.Routing;

/// <summary>Circuit-breaker state for one endpoint.</summary>
/// <param name="ConsecutiveFailures">Failures since the last success; drives exponential backoff.</param>
/// <param name="UnavailableUntil">The endpoint is skipped until this instant.</param>
/// <param name="Tripped">True while the endpoint is (or was, pending a half-open success) out of rotation.</param>
public sealed record EndpointHealth(int ConsecutiveFailures, DateTimeOffset UnavailableUntil, bool Tripped)
{
    public static EndpointHealth Healthy { get; } = new(0, DateTimeOffset.MinValue, false);
}

/// <summary>
/// Persists endpoint health (Repository). The default is per-process; implement this over
/// Redis or a database to share cooldown state across app instances, so one instance
/// tripping a dead deployment protects the whole fleet.
/// </summary>
public interface IEndpointHealthStore
{
    ValueTask<EndpointHealth> GetAsync(string endpointName, CancellationToken cancellationToken = default);

    ValueTask SetAsync(string endpointName, EndpointHealth health, CancellationToken cancellationToken = default);
}

/// <summary>In-process health store. Suitable for single-instance apps and tests.</summary>
public sealed class InMemoryEndpointHealthStore : IEndpointHealthStore
{
    private readonly ConcurrentDictionary<string, EndpointHealth> _health = new();

    public ValueTask<EndpointHealth> GetAsync(string endpointName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        return ValueTask.FromResult(_health.GetValueOrDefault(endpointName, EndpointHealth.Healthy));
    }

    public ValueTask SetAsync(string endpointName, EndpointHealth health, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        ArgumentNullException.ThrowIfNull(health);
        _health[endpointName] = health;
        return ValueTask.CompletedTask;
    }
}
