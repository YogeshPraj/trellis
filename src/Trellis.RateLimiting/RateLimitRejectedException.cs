namespace Trellis.RateLimiting;

/// <summary>
/// A request was refused by a local rate limit before it reached the provider.
/// </summary>
/// <remarks>
/// Distinct from a provider 429: this one cost nothing and never left the process, so it is
/// safe to surface immediately rather than failing over to another deployment.
/// </remarks>
public sealed class RateLimitRejectedException(string partition, TimeSpan? retryAfter)
    : Exception($"Rate limit reached for '{partition}'." +
                (retryAfter is TimeSpan wait ? $" Retry after {wait}." : string.Empty))
{
    /// <summary>The partition that was exhausted.</summary>
    public string Partition { get; } = partition;

    /// <summary>How long until capacity is expected, when the limiter reports it.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
