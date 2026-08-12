using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Trellis.Routing;

/// <summary>Observed performance of one endpoint (per process).</summary>
/// <param name="AverageLatencyMs">Exponential moving average of call latency; 0 when unmeasured.</param>
/// <param name="Successes">Successful calls.</param>
/// <param name="Failures">Failed calls.</param>
public sealed record EndpointMetricsSnapshot(double AverageLatencyMs, long Successes, long Failures);

/// <summary>What a selection strategy may consult when ordering a tier.</summary>
public interface ISelectionContext
{
    /// <summary>Monotonic per-request counter, for rotation-based strategies.</summary>
    int Rotation { get; }

    EndpointMetricsSnapshot MetricsFor(ModelEndpoint endpoint);

    /// <summary>
    /// Requests currently in flight to this endpoint from this process, counting a streaming
    /// response as in flight until its last token. Zero by default so existing implementations
    /// keep working; the router supplies real counts.
    /// </summary>
    int InFlightFor(ModelEndpoint endpoint) => 0;
}

/// <summary>
/// Orders the endpoints of one priority tier for a request (Strategy). Priorities
/// themselves always win first; strategies only arbitrate within a tier.
/// </summary>
public interface IEndpointSelectionStrategy
{
    IEnumerable<ModelEndpoint> OrderTier(IReadOnlyList<ModelEndpoint> tier, ISelectionContext context);
}

/// <summary>Even load sharing: rotates the tier per request. The default.</summary>
public sealed class RoundRobinSelectionStrategy : IEndpointSelectionStrategy
{
    public IEnumerable<ModelEndpoint> OrderTier(IReadOnlyList<ModelEndpoint> tier, ISelectionContext context)
    {
        if (tier.Count <= 1)
        {
            return tier;
        }
        int offset = Math.Abs(context.Rotation % tier.Count);
        return tier.Skip(offset).Concat(tier.Take(offset));
    }
}

/// <summary>
/// Prefers the endpoint with the lowest observed average latency. Unmeasured endpoints
/// sort first so every deployment gets sampled.
/// </summary>
public sealed class LowestLatencySelectionStrategy : IEndpointSelectionStrategy
{
    public IEnumerable<ModelEndpoint> OrderTier(IReadOnlyList<ModelEndpoint> tier, ISelectionContext context) =>
        tier.OrderBy(e => context.MetricsFor(e).AverageLatencyMs);
}

/// <summary>
/// Prefers the cheapest endpoint (by <see cref="ModelEndpoint.CostPerMillionTokens"/>;
/// endpoints without a declared cost sort last), breaking ties by observed latency.
/// </summary>
public sealed class LowestCostSelectionStrategy : IEndpointSelectionStrategy
{
    public IEnumerable<ModelEndpoint> OrderTier(IReadOnlyList<ModelEndpoint> tier, ISelectionContext context) =>
        tier.OrderBy(e => e.CostPerMillionTokens ?? double.MaxValue)
            .ThenBy(e => context.MetricsFor(e).AverageLatencyMs);
}

/// <summary>
/// Splits traffic in proportion to <see cref="ModelEndpoint.Weight"/> — for tiers whose
/// deployments have different provisioned capacity (say a PTU deployment alongside a
/// pay-as-you-go one).
/// </summary>
/// <remarks>
/// Uses smooth weighted round-robin (the algorithm nginx uses), not weighted random: for
/// weights 3 and 1 it yields A B A A rather than clustering three A's together, so the split
/// holds over short bursts instead of only in the long run. It is computed from the request
/// counter rather than kept in mutable state, so the strategy stays stateless and
/// deterministic.
/// </remarks>
public sealed class WeightedSelectionStrategy : IEndpointSelectionStrategy
{
    public IEnumerable<ModelEndpoint> OrderTier(IReadOnlyList<ModelEndpoint> tier, ISelectionContext context)
    {
        ArgumentNullException.ThrowIfNull(tier);
        ArgumentNullException.ThrowIfNull(context);
        if (tier.Count <= 1)
        {
            return tier;
        }

        int total = 0;
        foreach (ModelEndpoint endpoint in tier)
        {
            total += endpoint.Weight;
        }

        // Replay the schedule up to this request's position. Bounded by the summed weight,
        // which is a handful of iterations for any realistic tier.
        int steps = Math.Abs(context.Rotation % total) + 1;
        int[] current = new int[tier.Count];
        int chosen = 0;
        for (int step = 0; step < steps; step++)
        {
            chosen = 0;
            for (int i = 0; i < tier.Count; i++)
            {
                current[i] += tier[i].Weight;
                if (current[i] > current[chosen])
                {
                    chosen = i;
                }
            }
            current[chosen] -= total;
        }

        // The winner leads; the rest follow heaviest-first, so failover also prefers capacity.
        ModelEndpoint primary = tier[chosen];
        return new[] { primary }.Concat(
            tier.Where(e => !ReferenceEquals(e, primary)).OrderByDescending(e => e.Weight));
    }
}

/// <summary>
/// Prefers the endpoint with the fewest requests in flight — the strategy that actually
/// tracks congestion rather than inferring it from past latency.
/// </summary>
/// <remarks>
/// Useful when request costs vary wildly (a 200-token classification next to a 100k-token
/// summarization), where average latency misleads but queue depth does not. Counts are
/// per-process: with several instances each sees only its own load, so this balances a
/// single instance's concurrency rather than the fleet's.
/// </remarks>
public sealed class LeastLoadedSelectionStrategy : IEndpointSelectionStrategy
{
    public IEnumerable<ModelEndpoint> OrderTier(IReadOnlyList<ModelEndpoint> tier, ISelectionContext context)
    {
        ArgumentNullException.ThrowIfNull(tier);
        ArgumentNullException.ThrowIfNull(context);
        if (tier.Count <= 1)
        {
            return tier;
        }

        // Rotate first so that endpoints tied on load (notably all-zero at idle) still share
        // traffic instead of the first one always winning; OrderBy is stable, so the rotation
        // survives as the tie-break.
        int offset = Math.Abs(context.Rotation % tier.Count);
        return tier.Skip(offset).Concat(tier.Take(offset)).OrderBy(context.InFlightFor);
    }
}

/// <summary>Counts requests currently in flight per endpoint, for <see cref="LeastLoadedSelectionStrategy"/>.</summary>
internal sealed class InFlightTracker
{
    private readonly ConcurrentDictionary<string, StrongBox<int>> _counts = new();

    public int Count(string endpointName) =>
        _counts.TryGetValue(endpointName, out StrongBox<int>? box) ? Volatile.Read(ref box.Value) : 0;

    /// <summary>Marks one request in flight until the returned lease is disposed.</summary>
    public Lease Acquire(string endpointName)
    {
        StrongBox<int> box = _counts.GetOrAdd(endpointName, _ => new StrongBox<int>(0));
        Interlocked.Increment(ref box.Value);
        return new Lease(box);
    }

    /// <summary>Releases exactly once, however the request ends — success, failure, or abandonment.</summary>
    internal sealed class Lease(StrongBox<int> box) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Interlocked.Decrement(ref box.Value);
            }
        }
    }
}

/// <summary>Per-process latency/outcome tracking feeding <see cref="ISelectionContext"/>.</summary>
internal sealed class MetricsTracker
{
    private sealed class Entry
    {
        public readonly object Lock = new();
        public double EmaLatencyMs;
        public long Successes;
        public long Failures;
    }

    private const double Alpha = 0.2;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public void Record(string endpointName, double latencyMs, bool success)
    {
        Entry entry = _entries.GetOrAdd(endpointName, _ => new Entry());
        lock (entry.Lock)
        {
            entry.EmaLatencyMs = entry.EmaLatencyMs == 0 ? latencyMs : ((1 - Alpha) * entry.EmaLatencyMs) + (Alpha * latencyMs);
            if (success)
            {
                entry.Successes++;
            }
            else
            {
                entry.Failures++;
            }
        }
    }

    public EndpointMetricsSnapshot Snapshot(string endpointName)
    {
        if (!_entries.TryGetValue(endpointName, out Entry? entry))
        {
            return new EndpointMetricsSnapshot(0, 0, 0);
        }
        lock (entry.Lock)
        {
            return new EndpointMetricsSnapshot(entry.EmaLatencyMs, entry.Successes, entry.Failures);
        }
    }
}

internal sealed class SelectionContext(int rotation, MetricsTracker metrics, InFlightTracker inFlight) : ISelectionContext
{
    public int Rotation { get; } = rotation;

    public EndpointMetricsSnapshot MetricsFor(ModelEndpoint endpoint) => metrics.Snapshot(endpoint.Name);

    public int InFlightFor(ModelEndpoint endpoint) => inFlight.Count(endpoint.Name);
}
