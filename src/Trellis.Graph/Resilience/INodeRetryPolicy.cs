namespace Trellis.Graph.Resilience;

/// <summary>
/// Decides whether a failed node is retried (Strategy). Retries are opt-in per node, because
/// re-running a node re-runs its side effects — see <see cref="NodeResilience{TState}"/>.
/// </summary>
public interface INodeRetryPolicy
{
    ValueTask<NodeRetryDecision> EvaluateAsync(NodeFailureContext context, CancellationToken cancellationToken = default);
}
