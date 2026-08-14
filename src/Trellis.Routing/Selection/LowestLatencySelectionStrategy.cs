using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Trellis.Routing.Capabilities;

namespace Trellis.Routing.Selection;

/// <summary>
/// Prefers the endpoint with the lowest observed average latency. Unmeasured endpoints
/// sort first so every deployment gets sampled.
/// </summary>
public sealed class LowestLatencySelectionStrategy : IEndpointSelectionStrategy
{
    public IEnumerable<ModelEndpoint> OrderTier(IReadOnlyList<ModelEndpoint> tier, ISelectionContext context) =>
        tier.OrderBy(e => context.MetricsFor(e).AverageLatencyMs);
}
