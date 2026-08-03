using System.Globalization;
using Trellis.State;

namespace Trellis.Routing;

/// <summary>
/// Adapts any <see cref="ISharedStateStore"/> into an <see cref="IEndpointHealthStore"/>,
/// so a fleet of app instances shares one view of which deployments are cooling down.
/// Failure counting uses the store's atomic <see cref="ISharedStateStore.IncrementAsync"/> —
/// with an atomic provider (Redis) concurrent failures across instances never lose backoff
/// escalation. Cooldown windows are last-writer-wins, which is acceptable: concurrent
/// writers compute near-identical windows.
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
    /// Optional TTL for cooldown entries — a safety net that lets stale trip records expire
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
        string? failuresText = await _store.GetAsync(FailuresKey(endpointName), cancellationToken).ConfigureAwait(false);
        string? untilText = await _store.GetAsync(CooldownKey(endpointName), cancellationToken).ConfigureAwait(false);

        int failures = failuresText is not null && int.TryParse(failuresText, out int parsed) ? parsed : 0;
        DateTimeOffset until = untilText is not null
            && DateTimeOffset.TryParse(untilText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsedUntil)
                ? parsedUntil
                : DateTimeOffset.MinValue;
        return new EndpointHealth(failures, until, Tripped: untilText is not null);
    }

    public async ValueTask<int> RecordFailureAsync(string endpointName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        long failures = await _store.IncrementAsync(FailuresKey(endpointName), cancellationToken).ConfigureAwait(false);
        return (int)Math.Min(failures, int.MaxValue);
    }

    public async ValueTask SetCooldownAsync(string endpointName, DateTimeOffset until, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        await _store
            .SetAsync(CooldownKey(endpointName), until.ToString("O", CultureInfo.InvariantCulture), _entryTimeToLive, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask ResetAsync(string endpointName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        await _store.RemoveAsync(FailuresKey(endpointName), cancellationToken).ConfigureAwait(false);
        await _store.RemoveAsync(CooldownKey(endpointName), cancellationToken).ConfigureAwait(false);
    }

    private string FailuresKey(string endpointName) => _keyPrefix + endpointName + ":failures";

    private string CooldownKey(string endpointName) => _keyPrefix + endpointName + ":cooldown";
}
