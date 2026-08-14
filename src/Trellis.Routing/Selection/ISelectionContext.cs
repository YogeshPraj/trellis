using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Trellis.Routing.Capabilities;

namespace Trellis.Routing.Selection;

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
