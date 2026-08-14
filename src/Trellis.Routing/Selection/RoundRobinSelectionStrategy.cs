using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Trellis.Routing.Capabilities;

namespace Trellis.Routing.Selection;

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
