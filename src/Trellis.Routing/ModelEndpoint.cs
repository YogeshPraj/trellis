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
    public ModelEndpoint(string name, IChatClient client, int priority = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(client);
        Name = name;
        Client = client;
        Priority = priority;
    }

    public string Name { get; }

    public IChatClient Client { get; }

    public int Priority { get; }
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

    /// <summary>
    /// Decides whether an exception should trip the endpoint and fail over (true) or
    /// propagate to the caller (false — e.g. a malformed request that every model would reject).
    /// Defaults to <see cref="ModelRouter.DefaultShouldTrip"/>.
    /// </summary>
    public Func<Exception, bool> ShouldTrip { get; set; } = ModelRouter.DefaultShouldTrip;

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
