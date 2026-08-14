namespace Trellis.Graph.Resilience;

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
