using Microsoft.Extensions.AI;
using Trellis.Routing.Capabilities;
using Trellis.Routing.Failures;
using Trellis.Routing.Health;
using Trellis.Routing.Selection;

namespace Trellis.Routing;

/// <summary>Configuration for <see cref="ModelRouter"/>.</summary>
public sealed class ModelRouterOptions
{
    /// <summary>Cooldown after an endpoint's first failure. Doubles per consecutive failure.</summary>
    public TimeSpan BaseCooldown { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound for the exponential cooldown.</summary>
    public TimeSpan MaxCooldown { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Turns provider exceptions into typed failures (status codes, Retry-After).</summary>
    public IFailureClassifier FailureClassifier { get; set; } = new DefaultFailureClassifier();

    /// <summary>
    /// Maps failure kinds to actions — e.g. context-window overflows fail over without
    /// tripping the (healthy) endpoint, unknown errors propagate.
    /// </summary>
    public IFailurePolicy FailurePolicy { get; set; } = new DefaultFailurePolicy();

    /// <summary>
    /// Where circuit-breaker state lives. Swap the in-process default for a shared store
    /// (Redis, SQL) so all app instances agree on which deployments are cooling down.
    /// </summary>
    public IEndpointHealthStore HealthStore { get; set; } = new InMemoryEndpointHealthStore();

    /// <summary>How endpoints within one priority tier are ordered per request.</summary>
    public IEndpointSelectionStrategy SelectionStrategy { get; set; } = new RoundRobinSelectionStrategy();

    /// <summary>Behavior when every endpoint is cooling down. Default: try the soonest-recovering one.</summary>
    public AllTrippedBehavior AllTrippedBehavior { get; set; } = AllTrippedBehavior.TryAnyway;

    /// <summary>Called when an endpoint is taken out of rotation (name, cause, unavailable-until).</summary>
    public Action<ModelEndpoint, Exception, DateTimeOffset>? OnEndpointTripped { get; set; }

    /// <summary>Called when a previously tripped endpoint serves a request again.</summary>
    public Action<ModelEndpoint>? OnEndpointRecovered { get; set; }

    /// <summary>Clock, overridable for tests. Defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; set; }
}
