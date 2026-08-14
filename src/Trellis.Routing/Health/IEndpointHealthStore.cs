using System.Collections.Concurrent;
using Trellis.State;

namespace Trellis.Routing.Health;

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
