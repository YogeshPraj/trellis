using Microsoft.Extensions.AI;
using Trellis.Routing.Capabilities;
using Trellis.Routing.Failures;
using Trellis.Routing.Health;
using Trellis.Routing.Selection;

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
