namespace Trellis.Graph.Leasing;

/// <summary>
/// Grants exclusive right to run one graph thread id, so two instances cannot execute the
/// same workflow and interleave its checkpoints.
/// </summary>
/// <remarks>
/// <para>
/// A graph run reads a checkpoint, executes a node, and writes a checkpoint. Two runs doing
/// that concurrently on one thread id interleave: each writes state derived from a snapshot
/// the other has already superseded, and the workflow silently forks. The default
/// <see cref="InProcessRunLease"/> prevents that within one process; a distributed
/// implementation prevents it across a fleet.
/// </para>
/// <para><b>A lease alone is not sufficient.</b></para>
/// <para>
/// Any lease bounded by a timeout can be held by a process that has stopped running — a long
/// GC pause, a hypervisor freeze, a stalled write — for longer than the timeout. The lease
/// expires, another instance acquires it, and then the first instance wakes up still believing
/// it is the holder. No timeout can close that window, because the paused process cannot
/// observe time passing.
/// </para>
/// <para>
/// That is what <see cref="RunLeaseHandle.FencingToken"/> is for: it increases with every
/// acquisition, and an <see cref="Checkpointing.IFencedCheckpointer{TState}"/> refuses a write
/// carrying a token lower than one it has already seen. The revived holder's write is rejected
/// rather than applied — the storage layer, not the clock, is what makes the guarantee.
/// </para>
/// </remarks>
public interface IRunLease
{
    /// <summary>
    /// Acquires the lease for <paramref name="threadId"/>, or returns null if another holder
    /// has it. Implementations must not block waiting for a holder to finish: a caller that
    /// wants to wait can retry, whereas a caller that wants to fail fast cannot un-block.
    /// </summary>
    ValueTask<RunLeaseHandle?> TryAcquireAsync(string threadId, CancellationToken cancellationToken = default);
}
