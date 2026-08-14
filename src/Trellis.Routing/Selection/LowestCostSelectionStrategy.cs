using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Trellis.Routing.Capabilities;

namespace Trellis.Routing.Selection;

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
