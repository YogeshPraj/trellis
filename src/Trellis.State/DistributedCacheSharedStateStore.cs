using Microsoft.Extensions.Caching.Distributed;

namespace Trellis.State;

/// <summary>
/// Bridges <see cref="ISharedStateStore"/> onto any <see cref="IDistributedCache"/>
/// implementation (Adapter) — Redis, SQL Server, Cosmos, Garnet, and every other
/// existing IDistributedCache provider work without Trellis-specific code.
///
/// ⚠ Atomicity: IDistributedCache offers no atomic primitives, so
/// <see cref="IncrementAsync"/> and <see cref="AppendAsync"/> are emulated with
/// read-modify-write. Concurrent writers to the same key CAN lose updates. For
/// multi-instance deployments that append or increment concurrently, use
/// RedisSharedStateStore (Trellis.State.Redis), whose primitives are truly atomic.
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

    public async ValueTask<long> IncrementAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        string? current = await _cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
        long next = (current is not null && long.TryParse(current, out long parsed) ? parsed : 0) + 1;
        await _cache.SetStringAsync(key, next.ToString(), cancellationToken).ConfigureAwait(false);
        return next;
    }

    public async ValueTask<long> AppendAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        List<string> list = await ReadListAsync(key, cancellationToken).ConfigureAwait(false);
        list.Add(value);
        await _cache.SetStringAsync(key, System.Text.Json.JsonSerializer.Serialize(list), cancellationToken).ConfigureAwait(false);
        return list.Count;
    }

    public async ValueTask<IReadOnlyList<string>> GetListAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return await ReadListAsync(key, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<string>> ReadListAsync(string key, CancellationToken cancellationToken)
    {
        string? json = await _cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
        return json is null ? [] : System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
    }
}
