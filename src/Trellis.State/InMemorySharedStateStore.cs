using System.Collections.Concurrent;

namespace Trellis.State;

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
