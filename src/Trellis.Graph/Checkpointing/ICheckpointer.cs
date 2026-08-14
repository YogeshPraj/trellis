using System.Collections.Concurrent;

namespace Trellis.Graph.Checkpointing;

/// <summary>Persists graph progress so runs can resume across failures or process restarts.</summary>
public interface ICheckpointer<TState>
{
    Task SaveAsync(Checkpoint<TState> checkpoint, CancellationToken cancellationToken = default);

    /// <summary>Returns the latest checkpoint for a thread, or null if none exists.</summary>
    Task<Checkpoint<TState>?> LoadAsync(string threadId, CancellationToken cancellationToken = default);

    /// <summary>Returns every checkpoint recorded for a thread, oldest first.</summary>
    Task<IReadOnlyList<Checkpoint<TState>>> GetHistoryAsync(string threadId, CancellationToken cancellationToken = default);
}
