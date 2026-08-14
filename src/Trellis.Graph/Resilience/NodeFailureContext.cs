namespace Trellis.Graph.Resilience;

/// <summary>Context for one failed node attempt.</summary>
/// <param name="Node">The node that threw.</param>
/// <param name="Attempt">Which attempt just failed (1 = the first).</param>
/// <param name="Error">The exception it threw.</param>
public sealed record NodeFailureContext(string Node, int Attempt, Exception Error);
