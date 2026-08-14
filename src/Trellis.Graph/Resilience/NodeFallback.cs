namespace Trellis.Graph.Resilience;

/// <summary>Recovers from a failed node: given the node's input and the error, produce a state to carry on with.</summary>
public delegate Task<TState> NodeFallback<TState>(TState state, Exception error, CancellationToken cancellationToken);
