using System.Collections.Concurrent;

namespace Trellis.State;

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
