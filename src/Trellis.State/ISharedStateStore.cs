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
