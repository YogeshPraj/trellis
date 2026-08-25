using System.Collections.Concurrent;

namespace Trellis.Graph.Leasing;

/// <summary>
/// The default lease: exclusive within one process, and making no claim beyond it.
/// </summary>
/// <remarks>
/// <para>
/// This is enough for a single-process app and for tests, and it is what a graph uses when no
/// lease is supplied — so nothing changes for anyone not deploying multiple instances.
/// </para>
/// <para>
/// ⚠ In a multi-instance deployment it guards nothing: each instance has its own set, so every
/// instance will happily start the same thread id. Use a distributed <see cref="IRunLease"/>
/// there.
/// </para>
/// <para>
/// It deliberately issues <see cref="RunLeaseHandle.Unfenced"/> rather than a counter. A
/// per-process counter restarts at zero on restart and runs independently on every instance,
/// so tokens drawn from it are not globally ordered. Feeding those to a fenced checkpointer
/// would reject sound writes and accept stale ones — arbitrarily, and invisibly. Claiming no
/// ordering is strictly better than claiming a false one.
/// </para>
/// </remarks>
public sealed class InProcessRunLease : IRunLease
{
    private readonly ConcurrentDictionary<string, byte> _held = new(StringComparer.Ordinal);

    public ValueTask<RunLeaseHandle?> TryAcquireAsync(string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        return ValueTask.FromResult<RunLeaseHandle?>(
            _held.TryAdd(threadId, 0) ? new Handle(this, threadId) : null);
    }

    private sealed class Handle(InProcessRunLease owner, string threadId)
        : RunLeaseHandle(threadId, Unfenced)
    {
        // Never cancelled: an in-process lease is released only by its own holder, so there is
        // no third party that could take it away mid-run.
        public override CancellationToken Lost => CancellationToken.None;

        public override ValueTask DisposeAsync()
        {
            owner._held.TryRemove(ThreadId, out _);
            return ValueTask.CompletedTask;
        }
    }
}
