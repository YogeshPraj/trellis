namespace Trellis.Graph;

public enum GraphEventType
{
    /// <summary>A node is about to execute. <see cref="GraphEvent{TState}.State"/> is its input.</summary>
    NodeStarted,

    /// <summary>A node finished. <see cref="GraphEvent{TState}.State"/> is its output.</summary>
    NodeCompleted,

    /// <summary>The graph reached <see cref="StateGraph.End"/>. <see cref="GraphEvent{TState}.State"/> is final.</summary>
    GraphCompleted,
}

/// <summary>A single event emitted while streaming graph execution.</summary>
/// <param name="Type">What happened.</param>
/// <param name="Node">The node involved, or null for <see cref="GraphEventType.GraphCompleted"/>.</param>
/// <param name="Step">Number of node executions completed so far.</param>
/// <param name="State">The state at this point.</param>
/// <param name="Next">For <see cref="GraphEventType.NodeCompleted"/>, where the graph goes next.</param>
public sealed record GraphEvent<TState>(
    GraphEventType Type,
    string? Node,
    int Step,
    TState State,
    string? Next = null);

/// <summary>The result of running a graph to completion.</summary>
public sealed record GraphResult<TState>(TState FinalState, int Steps, string ThreadId);

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
}

/// <summary>The graph was built incorrectly (bad edges, missing entry point, ...).</summary>
public sealed class GraphDefinitionException(string message) : Exception(message);

/// <summary>The graph failed while executing.</summary>
public class GraphExecutionException(string message) : Exception(message);

/// <summary>The graph ran more steps than <see cref="GraphRunOptions.MaxSteps"/> allows.</summary>
public sealed class GraphRecursionException(string message) : GraphExecutionException(message);
