using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Trellis.Routing.Capabilities;

namespace Trellis.Routing.Selection;

internal sealed class SelectionContext(int rotation, MetricsTracker metrics, InFlightTracker inFlight) : ISelectionContext
{
    public int Rotation { get; } = rotation;

    public EndpointMetricsSnapshot MetricsFor(ModelEndpoint endpoint) => metrics.Snapshot(endpoint.Name);

    public int InFlightFor(ModelEndpoint endpoint) => inFlight.Count(endpoint.Name);
}
