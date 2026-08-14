using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Trellis.Routing.Capabilities;

namespace Trellis.Routing.Selection;

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
