using System.Collections.Concurrent;

namespace Trellis.State;

/// <summary>
/// A minimal cross-instance key-value contract: string values, optional TTL. Anything in
/// Trellis that needs state shared across app instances (router circuit-breaker health,
/// future coordination features) depends on this interface, never on a concrete backend.
/// Providers: <see cref="InMemorySharedStateStore"/> (per-process default),
/// <see cref="DistributedCacheSharedStateStore"/> (any IDistributedCache backend),
/// and RedisSharedStateStore in the Trellis.State.Redis package.
/// </summary>
public interface ISharedStateStore
{
    /// <summary>Returns the value for a key, or null when absent or expired.</summary>
    ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Sets a value, optionally expiring after <paramref name="timeToLive"/>.</summary>
    ValueTask SetAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default);

    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>In-process provider. The default; suitable for single-instance apps and tests.</summary>
public sealed class InMemorySharedStateStore(TimeProvider? timeProvider = null) : ISharedStateStore
{
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset? ExpiresAt)> _entries = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (_entries.TryGetValue(key, out (string Value, DateTimeOffset? ExpiresAt) entry))
        {
            if (entry.ExpiresAt is DateTimeOffset expiry && expiry <= _time.GetUtcNow())
            {
                _entries.TryRemove(key, out _);
                return ValueTask.FromResult<string?>(null);
            }
            return ValueTask.FromResult<string?>(entry.Value);
        }
        return ValueTask.FromResult<string?>(null);
    }

    public ValueTask SetAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        _entries[key] = (value, timeToLive is TimeSpan ttl ? _time.GetUtcNow() + ttl : null);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _entries.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }
}
