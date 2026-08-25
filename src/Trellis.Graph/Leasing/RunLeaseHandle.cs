namespace Trellis.Graph.Leasing;

/// <summary>
/// An acquired <see cref="IRunLease"/>. Disposing it releases the lease; losing it cancels
/// <see cref="Lost"/> so the run can stop instead of working on state it no longer owns.
/// </summary>
public abstract class RunLeaseHandle : IAsyncDisposable
{
    /// <summary>
    /// The token value meaning "this lease makes no cross-process ordering claim". A fenced
    /// checkpointer accepts these writes unconditionally rather than comparing them, because
    /// comparing tokens that are not globally ordered would reject valid writes at random.
    /// </summary>
    public const long Unfenced = 0;

    /// <param name="threadId">The graph thread this lease covers.</param>
    /// <param name="fencingToken">
    /// A token that increases with every acquisition of this thread id, or
    /// <see cref="Unfenced"/> when the implementation cannot make that guarantee.
    /// </param>
    protected RunLeaseHandle(string threadId, long fencingToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentOutOfRangeException.ThrowIfNegative(fencingToken);
        ThreadId = threadId;
        FencingToken = fencingToken;
    }

    /// <summary>The graph thread this lease covers.</summary>
    public string ThreadId { get; }

    /// <summary>
    /// Monotonically increasing across acquisitions of <see cref="ThreadId"/>, so a stale
    /// holder always carries a lower token than the holder that displaced it.
    /// <see cref="Unfenced"/> when the implementation cannot guarantee that ordering.
    /// </summary>
    public long FencingToken { get; }

    /// <summary>
    /// Cancelled when the lease is no longer held — expired, revoked, or its backing store
    /// unreachable for long enough that the holder can no longer prove ownership.
    /// </summary>
    public abstract CancellationToken Lost { get; }

    /// <summary>Releases the lease. Must be safe to call after the lease has already been lost.</summary>
    public abstract ValueTask DisposeAsync();
}
