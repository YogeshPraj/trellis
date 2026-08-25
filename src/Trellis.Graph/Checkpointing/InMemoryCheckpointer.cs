using System.Collections.Concurrent;
using Trellis.Graph.Leasing;

namespace Trellis.Graph.Checkpointing;

/// <summary>Keeps checkpoints in process memory. Suitable for tests and single-process apps.</summary>
/// <remarks>
/// Fencing is honoured so the fenced path can be exercised without a database, though in a
/// single process <see cref="InProcessRunLease"/> already makes concurrent runs impossible.
/// </remarks>
public sealed class InMemoryCheckpointer<TState> : IFencedCheckpointer<TState>
{
    private readonly ConcurrentDictionary<string, ThreadLog> _threads = new(StringComparer.Ordinal);

    /// <summary>One thread's checkpoints and the highest fencing token accepted for it.</summary>
    private sealed class ThreadLog
    {
        public List<Checkpoint<TState>> History { get; } = [];

        public long HighestFence { get; set; }
    }

    public Task SaveAsync(Checkpoint<TState> checkpoint, CancellationToken cancellationToken = default) =>
        SaveFencedAsync(checkpoint, RunLeaseHandle.Unfenced, cancellationToken);

    public Task SaveFencedAsync(
        Checkpoint<TState> checkpoint, long fencingToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ThreadLog thread = _threads.GetOrAdd(checkpoint.ThreadId, _ => new ThreadLog());
        lock (thread)
        {
            if (fencingToken != RunLeaseHandle.Unfenced)
            {
                if (fencingToken < thread.HighestFence)
                {
                    throw new RunLeaseLostException(checkpoint.ThreadId, fencingToken);
                }
                thread.HighestFence = fencingToken;
            }
            thread.History.Add(checkpoint);
        }
        return Task.CompletedTask;
    }

    public Task<Checkpoint<TState>?> LoadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        if (_threads.TryGetValue(threadId, out ThreadLog? thread))
        {
            lock (thread)
            {
                if (thread.History.Count > 0)
                {
                    return Task.FromResult<Checkpoint<TState>?>(thread.History[^1]);
                }
            }
        }
        return Task.FromResult<Checkpoint<TState>?>(null);
    }

    public Task<IReadOnlyList<Checkpoint<TState>>> GetHistoryAsync(string threadId, CancellationToken cancellationToken = default)
    {
        if (_threads.TryGetValue(threadId, out ThreadLog? thread))
        {
            lock (thread)
            {
                return Task.FromResult<IReadOnlyList<Checkpoint<TState>>>([.. thread.History]);
            }
        }
        return Task.FromResult<IReadOnlyList<Checkpoint<TState>>>([]);
    }
}
