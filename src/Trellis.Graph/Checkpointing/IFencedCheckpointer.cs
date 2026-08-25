using Trellis.Graph.Leasing;

namespace Trellis.Graph.Checkpointing;

/// <summary>
/// Opt-in capability for checkpointers that can reject a write from a superseded lease holder.
/// </summary>
/// <remarks>
/// <para>
/// A graph tests for this interface and degrades explicitly when it is absent, rather than
/// assuming a guarantee the store cannot deliver — the same bargain
/// <c>IAtomicSharedStateStore</c> makes for compare-and-swap.
/// </para>
/// <para>
/// Without it, a lease is only as good as its timeout, and a process that stalls past its
/// timeout can still write. With it, that write is refused by the store itself: whichever
/// holder has the highest fencing token is the only one whose checkpoints are accepted, no
/// matter which one happens to be executing.
/// </para>
/// </remarks>
public interface IFencedCheckpointer<TState> : ICheckpointer<TState>
{
    /// <summary>
    /// Saves <paramref name="checkpoint"/> only if no checkpoint for that thread has already
    /// been accepted under a higher fencing token. A token of
    /// <see cref="RunLeaseHandle.Unfenced"/> is saved unconditionally.
    /// </summary>
    /// <exception cref="RunLeaseLostException">
    /// A higher token has been seen, so this caller is a superseded holder and its state is
    /// stale. Implementations must make the check and the write one atomic operation:
    /// checking first and writing second reintroduces the race this exists to close.
    /// </exception>
    Task SaveFencedAsync(Checkpoint<TState> checkpoint, long fencingToken, CancellationToken cancellationToken = default);
}
