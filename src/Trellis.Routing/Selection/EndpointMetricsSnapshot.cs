using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Trellis.Routing.Capabilities;

namespace Trellis.Routing.Selection;

/// <summary>Observed performance of one endpoint (per process).</summary>
/// <param name="AverageLatencyMs">Exponential moving average of call latency; 0 when unmeasured.</param>
/// <param name="Successes">Successful calls.</param>
/// <param name="Failures">Failed calls.</param>
public sealed record EndpointMetricsSnapshot(double AverageLatencyMs, long Successes, long Failures);
