using StackExchange.Redis;
using Trellis.State;

namespace Trellis.State.Redis;

/// <summary>
/// Redis provider for <see cref="ISharedStateStore"/>. Takes an injected
/// <see cref="IConnectionMultiplexer"/> — the application owns the connection's lifetime,
/// this store never creates or disposes one.
/// </summary>
public sealed class RedisSharedStateStore : ISharedStateStore
{
    private readonly IDatabase _database;
    private readonly string _keyPrefix;

    /// <param name="connection">The shared Redis connection.</param>
    /// <param name="keyPrefix">Prepended to every key to namespace Trellis state.</param>
    /// <param name="database">Redis database number; -1 uses the connection's default.</param>
    public RedisSharedStateStore(IConnectionMultiplexer connection, string keyPrefix = "trellis:", int database = -1)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(keyPrefix);
        _database = connection.GetDatabase(database);
        _keyPrefix = keyPrefix;
    }

    public async ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        RedisValue value = await _database.StringGetAsync(_keyPrefix + key).ConfigureAwait(false);
        return value.IsNull ? null : value.ToString();
    }

    public async ValueTask SetAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        Expiration expiration = timeToLive is TimeSpan ttl ? ttl : Expiration.Default;
        await _database.StringSetAsync(_keyPrefix + key, value, expiration).ConfigureAwait(false);
    }

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await _database.KeyDeleteAsync(_keyPrefix + key).ConfigureAwait(false);
    }
}
