using System.Collections.Concurrent;
using Trellis.State;

namespace Trellis.Routing.Health;

/// <summary>Circuit-breaker state for one endpoint.</summary>
/// <param name="ConsecutiveFailures">Failures since the last success; drives exponential backoff.</param>
/// <param name="UnavailableUntil">The endpoint is skipped until this instant.</param>
/// <param name="Tripped">True while the endpoint is (or was, pending a half-open success) out of rotation.</param>
public sealed record EndpointHealth(int ConsecutiveFailures, DateTimeOffset UnavailableUntil, bool Tripped)
{
    public static EndpointHealth Healthy { get; } = new(0, DateTimeOffset.MinValue, false);
}
