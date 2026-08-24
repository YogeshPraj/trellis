using System.Runtime.CompilerServices;
using System.Threading.RateLimiting;
using Microsoft.Extensions.AI;

namespace Trellis.RateLimiting;

/// <summary>
/// Applies a local rate limit before a request reaches the provider, partitioned by caller
/// and model.
/// </summary>
/// <remarks>
/// <para>
/// The router already reacts to a provider 429 by tripping the endpoint and failing over.
/// This is the other half: refusing locally, so the request never leaves the process, costs
/// nothing, and does not spend an endpoint's reputation on a failure you could predict.
/// </para>
/// <para>
/// Limits are per process. Several instances each enforce their own share, so a fleet-wide
/// limit means dividing the budget by instance count — or putting a shared limiter in front.
/// </para>
/// </remarks>
public sealed class RateLimitingChatClient : DelegatingChatClient, IAsyncDisposable
{
    private readonly PartitionedRateLimiter<RateLimitPartitionKey> _limiter;
    private readonly Func<ChatOptions?, RateLimitPartitionKey> _partitioner;
    private readonly bool _ownsLimiter;

    /// <param name="innerClient">The pipeline being wrapped.</param>
    /// <param name="limiter">The limiter; disposed with this client when <paramref name="ownsLimiter"/> is true.</param>
    /// <param name="partitioner">Decides which bucket a request belongs to.</param>
    /// <param name="ownsLimiter">Whether disposing this client disposes the limiter.</param>
    public RateLimitingChatClient(
        IChatClient innerClient,
        PartitionedRateLimiter<RateLimitPartitionKey> limiter,
        Func<ChatOptions?, RateLimitPartitionKey> partitioner,
        bool ownsLimiter = true)
        : base(innerClient)
    {
        _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
        _partitioner = partitioner ?? throw new ArgumentNullException(nameof(partitioner));
        _ownsLimiter = ownsLimiter;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using RateLimitLease lease = await AcquireAsync(options, cancellationToken).ConfigureAwait(false);
        return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The lease is held for the whole stream: a streaming call occupies the provider until
        // its last token, so releasing at first byte would undercount concurrent load.
        using RateLimitLease lease = await AcquireAsync(options, cancellationToken).ConfigureAwait(false);
        await foreach (ChatResponseUpdate update in base
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private async ValueTask<RateLimitLease> AcquireAsync(ChatOptions? options, CancellationToken cancellationToken)
    {
        RateLimitPartitionKey key = _partitioner(options);
        RateLimitLease lease = await _limiter.AcquireAsync(key, 1, cancellationToken).ConfigureAwait(false);
        if (lease.IsAcquired)
        {
            return lease;
        }

        lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter);
        lease.Dispose();
        throw new RateLimitRejectedException(key.ToString(), retryAfter == default ? null : retryAfter);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsLimiter)
        {
            await _limiter.DisposeAsync().ConfigureAwait(false);
        }
        Dispose();
    }
}
