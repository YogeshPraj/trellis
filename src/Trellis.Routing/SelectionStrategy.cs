using System.Collections.Concurrent;

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

internal sealed class SelectionContext(int rotation, MetricsTracker metrics) : ISelectionContext
{
    public int Rotation { get; } = rotation;

    public EndpointMetricsSnapshot MetricsFor(ModelEndpoint endpoint) => metrics.Snapshot(endpoint.Name);
}
