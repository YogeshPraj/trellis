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
/// Persists endpoint health (Repository). Failure counting is expressed as an atomic
/// operation (<see cref="RecordFailureAsync"/>) rather than read-modify-write, so
/// concurrent failures across app instances cannot lose backoff escalation. Implement over
/// a shared backend so one instance tripping a dead deployment protects the whole fleet.
/// </summary>
public interface IEndpointHealthStore
{
    ValueTask<EndpointHealth> GetAsync(string endpointName, CancellationToken cancellationToken = default);

    /// <summary>Atomically records a failure and returns the new consecutive-failure count.</summary>
    ValueTask<int> RecordFailureAsync(string endpointName, CancellationToken cancellationToken = default);

    /// <summary>Marks the endpoint out of rotation until <paramref name="until"/> (last-writer-wins is acceptable).</summary>
    ValueTask SetCooldownAsync(string endpointName, DateTimeOffset until, CancellationToken cancellationToken = default);

    /// <summary>Restores the endpoint to healthy after a successful call.</summary>
    ValueTask ResetAsync(string endpointName, CancellationToken cancellationToken = default);
}

/// <summary>In-process health store. Suitable for single-instance apps and tests.</summary>
public sealed class InMemoryEndpointHealthStore : IEndpointHealthStore
{
    private sealed class Entry
    {
        public int ConsecutiveFailures;
        public DateTimeOffset UnavailableUntil = DateTimeOffset.MinValue;
        public bool Tripped;
    }

    private readonly ConcurrentDictionary<string, Entry> _health = new();

    public ValueTask<EndpointHealth> GetAsync(string endpointName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        if (_health.TryGetValue(endpointName, out Entry? entry))
        {
            lock (entry)
            {
                return ValueTask.FromResult(new EndpointHealth(entry.ConsecutiveFailures, entry.UnavailableUntil, entry.Tripped));
            }
        }
        return ValueTask.FromResult(EndpointHealth.Healthy);
    }

    public ValueTask<int> RecordFailureAsync(string endpointName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        Entry entry = _health.GetOrAdd(endpointName, _ => new Entry());
        lock (entry)
        {
            return ValueTask.FromResult(++entry.ConsecutiveFailures);
        }
    }

    public ValueTask SetCooldownAsync(string endpointName, DateTimeOffset until, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        Entry entry = _health.GetOrAdd(endpointName, _ => new Entry());
        lock (entry)
        {
            entry.UnavailableUntil = until;
            entry.Tripped = true;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ResetAsync(string endpointName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        _health.TryRemove(endpointName, out _);
        return ValueTask.CompletedTask;
    }
}
