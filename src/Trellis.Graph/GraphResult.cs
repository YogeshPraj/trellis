using Trellis.Graph.Checkpointing;
using Trellis.Graph.Diagnostics;
using Trellis.Graph.Resilience;

namespace Trellis.Graph;

/// <summary>The result of a graph run.</summary>
/// <param name="FinalState">The state when the run ended (for interrupts: the state so far).</param>
/// <param name="Steps">Node executions completed.</param>
/// <param name="ThreadId">The thread id, usable to resume or inspect checkpoints.</param>
/// <param name="Status">Whether the run completed or paused at an interrupt.</param>
/// <param name="InterruptedBefore">The node the run paused in front of, when interrupted.</param>
public sealed record GraphResult<TState>(
    TState FinalState,
    int Steps,
    string ThreadId,
    GraphRunStatus Status = GraphRunStatus.Completed,
    string? InterruptedBefore = null);
