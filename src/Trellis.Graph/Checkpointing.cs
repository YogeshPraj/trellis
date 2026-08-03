using System.Collections.Concurrent;

namespace Trellis.Graph;

/// <summary>A snapshot of graph progress: the state after <paramref name="Step"/> node executions, about to run <paramref name="NextNode"/>.</summary>
public sealed record Checkpoint<TState>(string ThreadId, int Step, string NextNode, TState State);

/// <summary>Persists graph progress so runs can resume across failures or process restarts.</summary>
public interface ICheckpointer<TState>
{
    Task SaveAsync(Checkpoint<TState> checkpoint, CancellationToken cancellationToken = default);

    /// <summary>Returns the latest checkpoint for a thread, or null if none exists.</summary>
    Task<Checkpoint<TState>?> LoadAsync(string threadId, CancellationToken cancellationToken = default);

    /// <summary>Returns every checkpoint recorded for a thread, oldest first.</summary>
    Task<IReadOnlyList<Checkpoint<TState>>> GetHistoryAsync(string threadId, CancellationToken cancellationToken = default);
}

/// <summary>Keeps checkpoints in process memory. Suitable for tests and single-process apps.</summary>
public sealed class InMemoryCheckpointer<TState> : ICheckpointer<TState>
{
    private readonly ConcurrentDictionary<string, List<Checkpoint<TState>>> _threads = new();

    public Task SaveAsync(Checkpoint<TState> checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        List<Checkpoint<TState>> history = _threads.GetOrAdd(checkpoint.ThreadId, _ => []);
        lock (history)
        {
            history.Add(checkpoint);
        }
        return Task.CompletedTask;
    }

    public Task<Checkpoint<TState>?> LoadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        if (_threads.TryGetValue(threadId, out List<Checkpoint<TState>>? history))
        {
            lock (history)
            {
                if (history.Count > 0)
                {
                    return Task.FromResult<Checkpoint<TState>?>(history[^1]);
                }
            }
        }
        return Task.FromResult<Checkpoint<TState>?>(null);
    }

    public Task<IReadOnlyList<Checkpoint<TState>>> GetHistoryAsync(string threadId, CancellationToken cancellationToken = default)
    {
        if (_threads.TryGetValue(threadId, out List<Checkpoint<TState>>? history))
        {
            lock (history)
            {
                return Task.FromResult<IReadOnlyList<Checkpoint<TState>>>([.. history]);
            }
        }
        return Task.FromResult<IReadOnlyList<Checkpoint<TState>>>([]);
    }
}
