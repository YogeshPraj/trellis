using System.Text.Json;
using Trellis.State;

namespace Trellis.Routing;

/// <summary>
/// Adapts any <see cref="ISharedStateStore"/> into an <see cref="IEndpointHealthStore"/>,
/// so a fleet of app instances shares one view of which deployments are cooling down:
/// one instance tripping a dead model protects all of them.
/// </summary>
/// <remarks>
/// <code>
/// var options = new ModelRouterOptions
/// {
///     HealthStore = new SharedStateEndpointHealthStore(
///         new RedisSharedStateStore(connectionMultiplexer)),
/// };
/// </code>
/// </remarks>
public sealed class SharedStateEndpointHealthStore : IEndpointHealthStore
{
    private readonly ISharedStateStore _store;
    private readonly string _keyPrefix;
    private readonly TimeSpan? _entryTimeToLive;

    /// <param name="store">The shared backend (Redis, IDistributedCache bridge, ...).</param>
    /// <param name="keyPrefix">Namespace for health entries within the store.</param>
    /// <param name="entryTimeToLive">
    /// Optional TTL for health entries — a safety net that lets stale trip records expire
    /// even if no request ever revisits the endpoint. Should comfortably exceed MaxCooldown.
    /// </param>
    public SharedStateEndpointHealthStore(
        ISharedStateStore store,
        string keyPrefix = "health:",
        TimeSpan? entryTimeToLive = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keyPrefix);
        _store = store;
        _keyPrefix = keyPrefix;
        _entryTimeToLive = entryTimeToLive;
    }

    public async ValueTask<EndpointHealth> GetAsync(string endpointName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        string? json = await _store.GetAsync(_keyPrefix + endpointName, cancellationToken).ConfigureAwait(false);
        return json is null
            ? EndpointHealth.Healthy
            : JsonSerializer.Deserialize<EndpointHealth>(json) ?? EndpointHealth.Healthy;
    }

    public async ValueTask SetAsync(string endpointName, EndpointHealth health, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        ArgumentNullException.ThrowIfNull(health);
        await _store
            .SetAsync(_keyPrefix + endpointName, JsonSerializer.Serialize(health), _entryTimeToLive, cancellationToken)
            .ConfigureAwait(false);
    }
}
