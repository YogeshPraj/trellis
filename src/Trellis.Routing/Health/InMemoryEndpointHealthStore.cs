using System.Collections.Concurrent;
using Trellis.State;

namespace Trellis.Routing.Health;

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
