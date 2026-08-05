using System.Collections.Concurrent;

namespace Trellis.State;

/// <summary>
/// A minimal cross-instance state contract. Anything in Trellis that needs state shared
/// across app instances (router circuit-breaker health, conversation archives) depends on
/// this interface, never on a concrete backend.
///
/// Keys are typed by usage: a key used with <see cref="SetAsync"/>/<see cref="IncrementAsync"/>
/// holds a string value; a key used with <see cref="AppendAsync"/>/<see cref="GetListAsync"/>
/// holds a list. Do not mix the two operation families on one key (Redis enforces this).
///
/// Atomicity is provider-dependent and documented per provider:
/// <see cref="InMemorySharedStateStore"/> and RedisSharedStateStore make
/// <see cref="IncrementAsync"/> and <see cref="AppendAsync"/> atomic;
/// <see cref="DistributedCacheSharedStateStore"/> emulates them with read-modify-write
/// and is only safe for single-writer scenarios.
/// </summary>
public interface ISharedStateStore
{
    /// <summary>Returns the value for a key, or null when absent or expired.</summary>
    ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Sets a value, optionally expiring after <paramref name="timeToLive"/>.</summary>
    ValueTask SetAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default);

    /// <summary>Removes a key (value or list).</summary>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically increments the integer stored at <paramref name="key"/> (0 when absent)
    /// and returns the new value. Atomic per provider documentation.
    /// </summary>
    ValueTask<long> IncrementAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically appends an element to the list at <paramref name="key"/> and returns the
    /// new list length. Atomic per provider documentation.
    /// </summary>
    ValueTask<long> AppendAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>Returns all elements of the list at <paramref name="key"/>, oldest first.</summary>
    ValueTask<IReadOnlyList<string>> GetListAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Opt-in capability for providers that can compare-and-swap. Callers that need
/// lost-update protection (see <c>SharedStateConversationStore</c>) test for this interface
/// and degrade explicitly when it is absent, rather than silently assuming atomicity that
/// a backend cannot deliver.
/// </summary>
public interface IAtomicSharedStateStore : ISharedStateStore
{
    /// <summary>
    /// Sets <paramref name="key"/> to <paramref name="newValue"/> only if its current value
    /// is exactly <paramref name="expectedValue"/> (null meaning "the key must not exist"),
    /// and reports whether the swap happened. Atomic across instances.
    /// </summary>
    ValueTask<bool> TrySetIfUnchangedAsync(
        string key,
        string? expectedValue,
        string newValue,
        TimeSpan? timeToLive = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// In-process provider. Increments, appends, and compare-and-swap are lock-protected
/// (atomic within the process). Suitable for single-instance apps and tests; expired
/// entries are purged on read and swept periodically on write so unread keys don't leak.
/// </summary>
public sealed class InMemorySharedStateStore(TimeProvider? timeProvider = null) : IAtomicSharedStateStore
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset? ExpiresAt)> _strings = new();
    private readonly ConcurrentDictionary<string, List<string>> _lists = new();
    private readonly Lock _incrementLock = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _lastSweepTicks;

    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (_strings.TryGetValue(key, out (string Value, DateTimeOffset? ExpiresAt) entry))
        {
            if (entry.ExpiresAt is DateTimeOffset expiry && expiry <= _time.GetUtcNow())
            {
                _strings.TryRemove(key, out _);
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
        _strings[key] = (value, timeToLive is TimeSpan ttl ? _time.GetUtcNow() + ttl : null);
        SweepIfDue();
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _strings.TryRemove(key, out _);
        _lists.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<long> IncrementAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_incrementLock)
        {
            long current = _strings.TryGetValue(key, out (string Value, DateTimeOffset? ExpiresAt) entry)
                && long.TryParse(entry.Value, out long parsed) ? parsed : 0;
            long next = current + 1;
            _strings[key] = (next.ToString(), null);
            return ValueTask.FromResult(next);
        }
    }

    public ValueTask<long> AppendAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        List<string> list = _lists.GetOrAdd(key, _ => []);
        lock (list)
        {
            list.Add(value);
            return ValueTask.FromResult((long)list.Count);
        }
    }

    public ValueTask<IReadOnlyList<string>> GetListAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (_lists.TryGetValue(key, out List<string>? list))
        {
            lock (list)
            {
                return ValueTask.FromResult<IReadOnlyList<string>>([.. list]);
            }
        }
        return ValueTask.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>Atomic within the process (shares the increment lock).</summary>
    public ValueTask<bool> TrySetIfUnchangedAsync(
        string key,
        string? expectedValue,
        string newValue,
        TimeSpan? timeToLive = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(newValue);
        lock (_incrementLock)
        {
            string? current = null;
            if (_strings.TryGetValue(key, out (string Value, DateTimeOffset? ExpiresAt) entry)
                && (entry.ExpiresAt is not DateTimeOffset expiry || expiry > _time.GetUtcNow()))
            {
                current = entry.Value;
            }
            if (current != expectedValue)
            {
                return ValueTask.FromResult(false);
            }
            _strings[key] = (newValue, timeToLive is TimeSpan ttl ? _time.GetUtcNow() + ttl : null);
            return ValueTask.FromResult(true);
        }
    }

    /// <summary>Purges expired string entries at most once per <see cref="SweepInterval"/>.</summary>
    private void SweepIfDue()
    {
        DateTimeOffset now = _time.GetUtcNow();
        long last = Interlocked.Read(ref _lastSweepTicks);
        if (now.UtcTicks - last < SweepInterval.Ticks
            || Interlocked.CompareExchange(ref _lastSweepTicks, now.UtcTicks, last) != last)
        {
            return;
        }
        foreach ((string key, (string, DateTimeOffset? ExpiresAt) entry) in _strings)
        {
            if (entry.ExpiresAt is DateTimeOffset expiry && expiry <= now)
            {
                _strings.TryRemove(key, out _);
            }
        }
    }
}
