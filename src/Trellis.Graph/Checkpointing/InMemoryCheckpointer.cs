using System.Collections.Concurrent;

namespace Trellis.Graph.Checkpointing;

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
