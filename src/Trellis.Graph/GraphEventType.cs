using Trellis.Graph.Checkpointing;
using Trellis.Graph.Diagnostics;
using Trellis.Graph.Resilience;

namespace Trellis.Graph;

public enum GraphEventType
{
    /// <summary>A node is about to execute. <see cref="GraphEvent{TState}.State"/> is its input.</summary>
    NodeStarted,

    /// <summary>A node finished. <see cref="GraphEvent{TState}.State"/> is its output.</summary>
    NodeCompleted,

    /// <summary>
    /// A node attempt failed and will be retried (see <see cref="NodeResilience{TState}"/>).
    /// <see cref="GraphEvent{TState}.Attempt"/> is the attempt that failed and
    /// <see cref="GraphEvent{TState}.Error"/> is why; <see cref="GraphEvent{TState}.State"/>
    /// is the input the retry will use.
    /// </summary>
    NodeRetrying,

    /// <summary>
    /// Every attempt failed and the node's fallback produced a state instead.
    /// <see cref="GraphEvent{TState}.Error"/> is the failure it recovered from.
    /// </summary>
    NodeFallbackApplied,

    /// <summary>The graph reached <see cref="StateGraph.End"/>. <see cref="GraphEvent{TState}.State"/> is final.</summary>
    GraphCompleted,

    /// <summary>
    /// Execution paused before <see cref="GraphEvent{TState}.Node"/> (listed in
    /// <see cref="GraphRunOptions.InterruptBefore"/>). A checkpoint was saved; rerun with the
    /// same thread id to resume, optionally after <see cref="CompiledGraph{TState}.UpdateStateAsync"/>.
    /// </summary>
    GraphInterrupted,
}
