using Microsoft.Extensions.Caching.Distributed;

namespace Trellis.State;

/// <summary>
/// Bridges <see cref="ISharedStateStore"/> onto any <see cref="IDistributedCache"/>
/// implementation (Adapter) — Redis, SQL Server, Cosmos, Garnet, and every other
/// existing IDistributedCache provider work without Trellis-specific code.
/// </summary>
public sealed class DistributedCacheSharedStateStore : ISharedStateStore
{
    private readonly IDistributedCache _cache;

    public DistributedCacheSharedStateStore(IDistributedCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    public async ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return await _cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        var options = new DistributedCacheEntryOptions();
        if (timeToLive is TimeSpan ttl)
        {
            options.AbsoluteExpirationRelativeToNow = ttl;
        }
        await _cache.SetStringAsync(key, value, options, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
    }
}
