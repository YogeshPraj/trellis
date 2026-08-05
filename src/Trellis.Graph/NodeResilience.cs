namespace Trellis.Graph;

/// <summary>Recovers from a failed node: given the node's input and the error, produce a state to carry on with.</summary>
public delegate Task<TState> NodeFallback<TState>(TState state, Exception error, CancellationToken cancellationToken);

/// <summary>Context for one failed node attempt.</summary>
/// <param name="Node">The node that threw.</param>
/// <param name="Attempt">Which attempt just failed (1 = the first).</param>
/// <param name="Error">The exception it threw.</param>
public sealed record NodeFailureContext(string Node, int Attempt, Exception Error);

/// <summary>What to do after a node attempt failed.</summary>
public readonly record struct NodeRetryDecision(bool ShouldRetry, TimeSpan Delay)
{
    /// <summary>Give up: run the fallback if one is configured, otherwise rethrow.</summary>
    public static NodeRetryDecision Stop => new(false, TimeSpan.Zero);

    /// <summary>Retry immediately.</summary>
    public static NodeRetryDecision Retry => new(true, TimeSpan.Zero);

    /// <summary>Retry after waiting.</summary>
    public static NodeRetryDecision RetryAfter(TimeSpan delay) => new(true, delay);
}

/// <summary>
/// Decides whether a failed node is retried (Strategy). Retries are opt-in per node, because
/// re-running a node re-runs its side effects — see <see cref="NodeResilience{TState}"/>.
/// </summary>
public interface INodeRetryPolicy
{
    ValueTask<NodeRetryDecision> EvaluateAsync(NodeFailureContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default policy: capped exponential backoff with jitter, up to <c>maxAttempts</c> total
/// attempts (first try included).
/// </summary>
/// <param name="maxAttempts">Total attempts including the first (default 3 = 1 try + 2 retries).</param>
/// <param name="baseDelay">Delay before the first retry; doubles each time (default 200ms).</param>
/// <param name="maxDelay">Ceiling for the backoff (default 30s).</param>
/// <param name="jitterFactor">
/// Random ± fraction applied to each delay (default 0.2). Jitter stops a fleet of workers
/// retrying a shared dependency in lockstep; set 0 for deterministic delays.
/// </param>
/// <param name="shouldRetry">
/// Optional filter — return false for errors that will never succeed on a retry
/// (bad input, authorization failures). Defaults to retrying every exception.
/// </param>
public sealed class ExponentialBackoffRetryPolicy(
    int maxAttempts = 3,
    TimeSpan? baseDelay = null,
    TimeSpan? maxDelay = null,
    double jitterFactor = 0.2,
    Func<Exception, bool>? shouldRetry = null) : INodeRetryPolicy
{
    private readonly int _maxAttempts = maxAttempts >= 1
        ? maxAttempts
        : throw new ArgumentOutOfRangeException(nameof(maxAttempts), "At least one attempt is required.");

    private readonly double _jitterFactor = jitterFactor is >= 0 and < 1
        ? jitterFactor
        : throw new ArgumentOutOfRangeException(nameof(jitterFactor), "Jitter must be in [0, 1).");

    private readonly TimeSpan _baseDelay = baseDelay ?? TimeSpan.FromMilliseconds(200);
    private readonly TimeSpan _maxDelay = maxDelay ?? TimeSpan.FromSeconds(30);

    public ValueTask<NodeRetryDecision> EvaluateAsync(
        NodeFailureContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Attempt >= _maxAttempts || shouldRetry?.Invoke(context.Error) == false)
        {
            return new(NodeRetryDecision.Stop);
        }

        // 2^(attempt-1) grows fast; compute in doubles and clamp before converting back.
        double milliseconds = Math.Min(
            _baseDelay.TotalMilliseconds * Math.Pow(2, context.Attempt - 1),
            _maxDelay.TotalMilliseconds);
        if (_jitterFactor > 0)
        {
            milliseconds *= 1 + ((Random.Shared.NextDouble() * 2 - 1) * _jitterFactor);
        }
        return new(NodeRetryDecision.RetryAfter(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds))));
    }
}

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
