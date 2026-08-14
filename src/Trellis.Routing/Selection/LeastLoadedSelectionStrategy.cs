using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Trellis.Routing.Capabilities;

namespace Trellis.Routing.Selection;

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
