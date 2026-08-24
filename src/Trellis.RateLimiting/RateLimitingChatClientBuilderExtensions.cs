using System.Threading.RateLimiting;
using Microsoft.Extensions.AI;

namespace Trellis.RateLimiting;

/// <summary>Adds proactive rate limiting to a chat client pipeline.</summary>
public static class RateLimitingChatClientBuilderExtensions
{
    /// <summary>Limits requests using a limiter you construct and partition yourself.</summary>
    public static ChatClientBuilder UseRateLimit(
        this ChatClientBuilder builder,
        PartitionedRateLimiter<RateLimitPartitionKey> limiter,
        Func<ChatOptions?, RateLimitPartitionKey> partitioner,
        bool ownsLimiter = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use(inner => new RateLimitingChatClient(inner, limiter, partitioner, ownsLimiter));
    }

    /// <summary>
    /// Limits each caller to a sliding budget of requests, refusing rather than queueing.
    /// </summary>
    /// <remarks>
    /// Refusing is the default because queueing an LLM request behind a rate limit usually
    /// just moves the timeout: the caller waits, then fails anyway. Build the limiter yourself
    /// if you want a queue.
    /// </remarks>
    /// <param name="builder">The pipeline being built.</param>
    /// <param name="requestsPerWindow">Requests allowed per window, per partition.</param>
    /// <param name="window">Length of the window.</param>
    /// <param name="subjectSelector">Identifies the caller; null applies one shared budget.</param>
    /// <param name="perModel">Whether each model gets its own budget.</param>
    public static ChatClientBuilder UseRateLimit(
        this ChatClientBuilder builder,
        int requestsPerWindow,
        TimeSpan window,
        Func<ChatOptions?, string?>? subjectSelector = null,
        bool perModel = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestsPerWindow);

        PartitionedRateLimiter<RateLimitPartitionKey> limiter =
            PartitionedRateLimiter.Create<RateLimitPartitionKey, string>(
                key => RateLimitPartition.GetFixedWindowLimiter(
                    key.ToString(),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = requestsPerWindow,
                        Window = window,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));

        return builder.UseRateLimit(
            limiter,
            options => new RateLimitPartitionKey(
                subjectSelector?.Invoke(options),
                perModel ? options?.ModelId : null));
    }
}
