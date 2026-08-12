using Microsoft.Extensions.AI;

namespace Trellis.Routing;

/// <summary>One model deployment behind the router.</summary>
public sealed class ModelEndpoint
{
    /// <param name="name">Human-readable name used in callbacks and error messages.</param>
    /// <param name="client">The chat client for this deployment.</param>
    /// <param name="priority">
    /// Lower is preferred. Endpoints with equal priority share load round-robin;
    /// higher-priority tiers are only used when every lower tier is cooling down.
    /// </param>
    /// <param name="capabilities">
    /// What this deployment supports. Requests needing an unsupported feature skip it.
    /// Defaults to <see cref="ModelCapabilities.Default"/>.
    /// </param>
    public ModelEndpoint(string name, IChatClient client, int priority = 0, ModelCapabilities? capabilities = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(client);
        Name = name;
        Client = client;
        Priority = priority;
        Capabilities = capabilities ?? ModelCapabilities.Default;
    }

    public string Name { get; }

    public IChatClient Client { get; }

    public int Priority { get; }

    public ModelCapabilities Capabilities { get; }

    /// <summary>
    /// Blended price per million tokens, used by <see cref="LowestCostSelectionStrategy"/>.
    /// Endpoints without a declared cost are considered most expensive.
    /// </summary>
    public double? CostPerMillionTokens { get; init; }

    /// <summary>
    /// Relative share of traffic within its priority tier, used by
    /// <see cref="WeightedSelectionStrategy"/> (default 1 — an equal share). A deployment
    /// with weight 3 receives three times the requests of one with weight 1.
    /// Ignored by every other selection strategy.
    /// </summary>
    public int Weight
    {
        get => _weight;
        init => _weight = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Weight must be at least 1.");
    }

    private readonly int _weight = 1;
}

/// <summary>What the router does when every endpoint is cooling down.</summary>
public enum AllTrippedBehavior
{
    /// <summary>Try the endpoint whose cooldown expires soonest anyway (graceful degradation).</summary>
    TryAnyway,

    /// <summary>Fail fast with <see cref="AllModelsUnavailableException"/>.</summary>
    Throw,
}

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

/// <summary>Thrown when no endpoint could serve the request.</summary>
public sealed class AllModelsUnavailableException : Exception
{
    public AllModelsUnavailableException(string message, IReadOnlyList<Exception> attempts)
        : base(message, attempts.Count > 0 ? new AggregateException(attempts) : null)
    {
        Attempts = attempts;
    }

    /// <summary>The failure from each endpoint that was attempted for this request.</summary>
    public IReadOnlyList<Exception> Attempts { get; }
}
