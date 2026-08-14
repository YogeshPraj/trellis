namespace Trellis.Graph.Resilience;

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
