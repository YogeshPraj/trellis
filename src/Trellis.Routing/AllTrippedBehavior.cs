using Microsoft.Extensions.AI;
using Trellis.Routing.Capabilities;
using Trellis.Routing.Failures;
using Trellis.Routing.Health;
using Trellis.Routing.Selection;

namespace Trellis.Routing;

/// <summary>What the router does when every endpoint is cooling down.</summary>
public enum AllTrippedBehavior
{
    /// <summary>Try the endpoint whose cooldown expires soonest anyway (graceful degradation).</summary>
    TryAnyway,

    /// <summary>Fail fast with <see cref="AllModelsUnavailableException"/>.</summary>
    Throw,
}
