using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Trellis.Routing.Capabilities;

namespace Trellis.Routing.Selection;

/// <summary>
/// Orders the endpoints of one priority tier for a request (Strategy). Priorities
/// themselves always win first; strategies only arbitrate within a tier.
/// </summary>
public interface IEndpointSelectionStrategy
{
    IEnumerable<ModelEndpoint> OrderTier(IReadOnlyList<ModelEndpoint> tier, ISelectionContext context);
}
