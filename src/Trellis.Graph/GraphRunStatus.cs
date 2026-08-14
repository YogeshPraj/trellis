using Trellis.Graph.Checkpointing;
using Trellis.Graph.Diagnostics;
using Trellis.Graph.Resilience;

namespace Trellis.Graph;

/// <summary>How a graph run ended.</summary>
public enum GraphRunStatus
{
    /// <summary>The graph reached <see cref="StateGraph.End"/>.</summary>
    Completed,

    /// <summary>The run paused at an interrupt; resume by rerunning with the same thread id.</summary>
    Interrupted,
}
