using Trellis.Graph.Checkpointing;
using Trellis.Graph.Diagnostics;
using Trellis.Graph.Resilience;

namespace Trellis.Graph;

/// <summary>A single event emitted while streaming graph execution.</summary>
/// <param name="Type">What happened.</param>
/// <param name="Node">The node involved, or null for <see cref="GraphEventType.GraphCompleted"/>.</param>
/// <param name="Step">Number of node executions completed so far.</param>
/// <param name="State">The state at this point.</param>
/// <param name="Next">For <see cref="GraphEventType.NodeCompleted"/>, where the graph goes next.</param>
/// <param name="Attempt">Which attempt this concerns; 1 unless retries are in play.</param>
/// <param name="Error">The failure behind a retry or fallback event; null otherwise.</param>
public sealed record GraphEvent<TState>(
    GraphEventType Type,
    string? Node,
    int Step,
    TState State,
    string? Next = null,
    int Attempt = 1,
    Exception? Error = null);
