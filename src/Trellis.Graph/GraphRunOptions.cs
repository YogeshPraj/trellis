using Trellis.Graph.Checkpointing;
using Trellis.Graph.Diagnostics;
using Trellis.Graph.Resilience;

namespace Trellis.Graph;

/// <summary>Options for a single graph run.</summary>
public sealed class GraphRunOptions
{
    public const int DefaultMaxSteps = 50;

    /// <summary>
    /// Identifies this execution for checkpointing. Runs that reuse a thread id
    /// resume from that thread's latest checkpoint.
    /// </summary>
    public string? ThreadId { get; set; }

    /// <summary>Safety limit on node executions (guards against infinite loops).</summary>
    public int MaxSteps { get; set; } = DefaultMaxSteps;

    /// <summary>
    /// Nodes to pause in front of (human-in-the-loop). Requires a checkpointer and a
    /// <see cref="ThreadId"/>; the paused run resumes when rerun with the same thread id.
    /// </summary>
    public IReadOnlyCollection<string>? InterruptBefore { get; set; }
}
