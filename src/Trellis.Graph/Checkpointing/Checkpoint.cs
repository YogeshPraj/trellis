using System.Collections.Concurrent;

namespace Trellis.Graph.Checkpointing;

/// <summary>A snapshot of graph progress: the state after <paramref name="Step"/> node executions, about to run <paramref name="NextNode"/>.</summary>
public sealed record Checkpoint<TState>(string ThreadId, int Step, string NextNode, TState State);
