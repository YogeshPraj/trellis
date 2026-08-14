namespace Trellis.Graph.Resilience;

/// <summary>
/// Per-node failure handling: retry, then fall back. Both are optional and both are off by
/// default — a graph node is arbitrary user code, and re-running it repeats whatever it did
/// before it threw.
/// </summary>
/// <remarks>
/// ⚠ <b>Retries require idempotent nodes.</b> A node that charged a card, sent an email, or
/// appended to a store before failing will do it again on every retry. Make the node
/// idempotent (natural keys, upserts, an idempotency token in the state) before enabling
/// retries on it. Retries do not consume <see cref="GraphRunOptions.MaxSteps"/> — they are
/// re-executions of the same step — so the retry policy's own attempt cap is the only bound.
/// </remarks>
public sealed class NodeResilience<TState>
{
    /// <summary>Retry policy for this node; null means one attempt only.</summary>
    public INodeRetryPolicy? Retry { get; init; }

    /// <summary>
    /// Last resort once retries are exhausted: turn the error into a usable state (a default
    /// value, a degraded result, a flag the next node routes on). Null means rethrow.
    /// </summary>
    public NodeFallback<TState>? Fallback { get; init; }

    /// <summary>Retry with the default exponential backoff policy.</summary>
    public static NodeResilience<TState> WithRetry(int maxAttempts = 3) =>
        new() { Retry = new ExponentialBackoffRetryPolicy(maxAttempts) };

    /// <summary>Retry, then recover with <paramref name="fallback"/> if every attempt failed.</summary>
    public static NodeResilience<TState> WithRetryAndFallback(NodeFallback<TState> fallback, int maxAttempts = 3)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return new() { Retry = new ExponentialBackoffRetryPolicy(maxAttempts), Fallback = fallback };
    }
}
