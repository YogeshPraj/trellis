using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Trellis.Routing;

/// <summary>
/// An <see cref="IChatClient"/> that routes across multiple model deployments by priority,
/// with typed circuit-breaker failover and capability awareness. The router itself only
/// orchestrates; the moving parts are pluggable strategies on <see cref="ModelRouterOptions"/>:
/// <see cref="IFailureClassifier"/> (what went wrong), <see cref="IFailurePolicy"/> (what to
/// do about it), <see cref="IEndpointHealthStore"/> (where cooldown state lives — share it
/// across instances), and <see cref="IEndpointSelectionStrategy"/> (how a tier is ordered:
/// round-robin, lowest latency, lowest cost).
///
/// A tripped endpoint is skipped entirely until its cooldown (the provider's Retry-After
/// when known, exponential otherwise) expires, so failover adds zero latency to later
/// requests. Request-shaped failures — context-window overflow, content policy — fail over
/// WITHOUT tripping the endpoint, since the model itself is healthy.
///
/// Conversation state stays canonical on the client: callers pass full history with a
/// logical <see cref="ChatOptions.ConversationId"/>; endpoints with
/// <see cref="ModelFeatures.ServerConversationState"/> transparently receive only the
/// unsynced tail plus their provider id, and failover replays the full history.
/// </summary>
public sealed class ModelRouter : IChatClient
{
    private readonly struct Candidate(ModelEndpoint endpoint, EndpointHealth health)
    {
        public ModelEndpoint Endpoint { get; } = endpoint;
        public EndpointHealth Health { get; } = health;
    }

    private readonly struct Requirements
    {
        public bool NeedsTools { get; init; }
        public bool NeedsVision { get; init; }
        public bool NeedsJson { get; init; }
        public int EstimatedTokens { get; init; }

        public static Requirements From(IList<ChatMessage> messages, ChatOptions? options)
        {
            bool vision = false;
            int chars = 0;
            foreach (ChatMessage message in messages)
            {
                foreach (AIContent content in message.Contents)
                {
                    if (content is TextContent text)
                    {
                        chars += text.Text.Length;
                    }
                    else if ((content is DataContent data && data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        || (content is UriContent uri && uri.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
                    {
                        vision = true;
                    }
                }
            }
            return new Requirements
            {
                NeedsTools = options?.Tools is { Count: > 0 },
                NeedsJson = options?.ResponseFormat is ChatResponseFormatJson,
                NeedsVision = vision,
                EstimatedTokens = chars / 4,
            };
        }

        public override string ToString() =>
            $"tools={NeedsTools}, vision={NeedsVision}, json={NeedsJson}, ~{EstimatedTokens} tokens";
    }

    private readonly ModelEndpoint[] _endpoints;
    private readonly ModelRouterOptions _options;
    private readonly TimeProvider _time;
    private readonly ConversationSyncManager _conversations = new();
    private readonly MetricsTracker _metrics = new();
    private readonly InFlightTracker _inFlight = new();
    private int _rotation = -1;

    public ModelRouter(IReadOnlyList<ModelEndpoint> endpoints, ModelRouterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count == 0)
        {
            throw new ArgumentException("At least one endpoint is required.", nameof(endpoints));
        }
        _endpoints = [.. endpoints.OrderBy(e => e.Priority)];
        _options = options ?? new ModelRouterOptions();
        _time = _options.TimeProvider ?? TimeProvider.System;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        IList<ChatMessage> full = messages as IList<ChatMessage> ?? [.. messages];
        Requirements requirements = Requirements.From(full, options);
        ThrowIfNoneCompatible(requirements);
        List<Exception> attempts = [];

        foreach (Candidate candidate in await SelectAsync(requirements, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            (IList<ChatMessage> send, ChatOptions? sendOptions) =
                _conversations.Prepare(candidate.Endpoint, full, options, streaming: false);

            long started = _time.GetTimestamp();
            // The lease makes this endpoint's load visible to LeastLoadedSelectionStrategy
            // while the call is outstanding, and is released however the call ends.
            using InFlightTracker.Lease lease = _inFlight.Acquire(candidate.Endpoint.Name);
            try
            {
                ChatResponse response = await candidate.Endpoint.Client
                    .GetResponseAsync(send, sendOptions, cancellationToken)
                    .ConfigureAwait(false);
                _metrics.Record(candidate.Endpoint.Name, _time.GetElapsedTime(started).TotalMilliseconds, success: true);
                await MarkSuccessAsync(candidate, cancellationToken).ConfigureAwait(false);
                _conversations.Record(candidate.Endpoint, full, options, response);
                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!await HandleFailureAsync(candidate, ex, attempts, cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }
            }
        }

        throw await AllUnavailableAsync(attempts, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IList<ChatMessage> full = messages as IList<ChatMessage> ?? [.. messages];
        Requirements requirements = Requirements.From(full, options);
        ThrowIfNoneCompatible(requirements);
        List<Exception> attempts = [];

        foreach (Candidate candidate in await SelectAsync(requirements, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            (IList<ChatMessage> send, ChatOptions? sendOptions) =
                _conversations.Prepare(candidate.Endpoint, full, options, streaming: true);

            // Fail over only until the first token arrives; after that the stream is committed.
            IAsyncEnumerator<ChatResponseUpdate>? stream = null;
            bool hasFirst;
            ChatResponseUpdate? first = null;
            long started = _time.GetTimestamp();
            // A streaming call stays in flight until its last token, so this lease outlives
            // the connect phase and is released in every exit below.
            InFlightTracker.Lease lease = _inFlight.Acquire(candidate.Endpoint.Name);
            try
            {
                stream = candidate.Endpoint.Client
                    .GetStreamingResponseAsync(send, sendOptions, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                hasFirst = await stream.MoveNextAsync().ConfigureAwait(false);
                if (hasFirst)
                {
                    first = stream.Current;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lease.Dispose();
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                throw;
            }
            catch (Exception ex)
            {
                lease.Dispose();
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                if (!await HandleFailureAsync(candidate, ex, attempts, cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }
                continue;
            }

            _metrics.Record(candidate.Endpoint.Name, _time.GetElapsedTime(started).TotalMilliseconds, success: true);
            await MarkSuccessAsync(candidate, cancellationToken).ConfigureAwait(false);
            try
            {
                if (hasFirst)
                {
                    yield return first!;
                    while (await stream!.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield return stream.Current;
                    }
                }
            }
            finally
            {
                lease.Dispose();
                await stream!.DisposeAsync().ConfigureAwait(false);
            }
            yield break;
        }

        throw await AllUnavailableAsync(attempts, cancellationToken).ConfigureAwait(false);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType?.IsInstanceOfType(this) == true ? this : null;

    /// <summary>The router does not own its endpoints' clients; dispose those yourself.</summary>
    public void Dispose()
    {
    }

    /// <summary>
    /// Classifies a failure and applies the policy. Returns true when the router should
    /// move on to the next endpoint, false when the error must propagate.
    /// </summary>
    private async ValueTask<bool> HandleFailureAsync(
        Candidate candidate,
        Exception exception,
        List<Exception> attempts,
        CancellationToken cancellationToken)
    {
        FailureClassification classification = _options.FailureClassifier.Classify(exception);
        FailureAction action = _options.FailurePolicy.Decide(classification);
        if (action == FailureAction.Propagate)
        {
            return false;
        }

        _metrics.Record(candidate.Endpoint.Name, 0, success: false);
        attempts.Add(exception);
        if (action == FailureAction.FailoverAndTrip)
        {
            await TripAsync(candidate, classification, exception, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    private async ValueTask TripAsync(
        Candidate candidate,
        FailureClassification classification,
        Exception cause,
        CancellationToken cancellationToken)
    {
        // Atomic increment: concurrent failures across instances never lose backoff escalation.
        int failures = await _options.HealthStore
            .RecordFailureAsync(candidate.Endpoint.Name, cancellationToken)
            .ConfigureAwait(false);
        TimeSpan cooldown = classification.RetryAfter ?? ExponentialCooldown(failures);
        DateTimeOffset until = _time.GetUtcNow() + cooldown;
        await _options.HealthStore
            .SetCooldownAsync(candidate.Endpoint.Name, until, cancellationToken)
            .ConfigureAwait(false);
        _options.OnEndpointTripped?.Invoke(candidate.Endpoint, cause, until);
    }

    private async ValueTask MarkSuccessAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        if (!candidate.Health.Tripped && candidate.Health.ConsecutiveFailures == 0)
        {
            return;
        }
        await _options.HealthStore
            .ResetAsync(candidate.Endpoint.Name, cancellationToken)
            .ConfigureAwait(false);
        if (candidate.Health.Tripped)
        {
            _options.OnEndpointRecovered?.Invoke(candidate.Endpoint);
        }
    }

    private TimeSpan ExponentialCooldown(int consecutiveFailures)
    {
        double factor = Math.Pow(2, Math.Min(consecutiveFailures - 1, 10));
        return TimeSpan.FromTicks(Math.Min(
            (long)(_options.BaseCooldown.Ticks * factor),
            _options.MaxCooldown.Ticks));
    }

    /// <summary>
    /// Builds this request's attempt order: compatible + available endpoints by priority
    /// tier (each tier ordered by the selection strategy); when everything compatible is
    /// cooling down, either degrade to the soonest-recovering endpoints or return nothing,
    /// per <see cref="ModelRouterOptions.AllTrippedBehavior"/>.
    /// </summary>
    private async ValueTask<List<Candidate>> SelectAsync(Requirements requirements, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        List<Candidate> available = [];
        List<Candidate> coolingDown = [];
        foreach (ModelEndpoint endpoint in _endpoints)
        {
            if (!IsCompatible(endpoint.Capabilities, requirements))
            {
                continue;
            }
            EndpointHealth health = await _options.HealthStore.GetAsync(endpoint.Name, cancellationToken).ConfigureAwait(false);
            (health.UnavailableUntil <= now ? available : coolingDown).Add(new Candidate(endpoint, health));
        }

        var context = new SelectionContext(Interlocked.Increment(ref _rotation), _metrics, _inFlight);
        List<Candidate> order = [];
        foreach (IGrouping<int, Candidate> tierGroup in available.GroupBy(c => c.Endpoint.Priority).OrderBy(g => g.Key))
        {
            Dictionary<ModelEndpoint, Candidate> byEndpoint = tierGroup.ToDictionary(c => c.Endpoint);
            foreach (ModelEndpoint endpoint in _options.SelectionStrategy.OrderTier([.. byEndpoint.Keys], context))
            {
                if (byEndpoint.TryGetValue(endpoint, out Candidate candidate))
                {
                    order.Add(candidate);
                }
            }
        }

        if (available.Count == 0 && _options.AllTrippedBehavior == AllTrippedBehavior.TryAnyway)
        {
            order.AddRange(coolingDown.OrderBy(c => c.Health.UnavailableUntil));
        }
        return order;
    }

    private static bool IsCompatible(ModelCapabilities capabilities, Requirements requirements)
    {
        if (requirements.NeedsTools && !capabilities.Supports(ModelFeatures.FunctionCalling))
        {
            return false;
        }
        if (requirements.NeedsVision && !capabilities.Supports(ModelFeatures.Vision))
        {
            return false;
        }
        if (requirements.NeedsJson && !capabilities.Supports(ModelFeatures.JsonResponseFormat))
        {
            return false;
        }
        if (capabilities.MaxInputTokens is int max && requirements.EstimatedTokens > max)
        {
            return false;
        }
        return true;
    }

    private void ThrowIfNoneCompatible(Requirements requirements)
    {
        if (!_endpoints.Any(e => IsCompatible(e.Capabilities, requirements)))
        {
            throw new NoCompatibleModelException($"No registered endpoint supports this request ({requirements}).");
        }
    }

    private async ValueTask<AllModelsUnavailableException> AllUnavailableAsync(
        List<Exception> attempts,
        CancellationToken cancellationToken)
    {
        DateTimeOffset earliest = DateTimeOffset.MaxValue;
        foreach (ModelEndpoint endpoint in _endpoints)
        {
            EndpointHealth health = await _options.HealthStore.GetAsync(endpoint.Name, cancellationToken).ConfigureAwait(false);
            earliest = health.UnavailableUntil < earliest ? health.UnavailableUntil : earliest;
        }
        return new AllModelsUnavailableException(
            $"All {_endpoints.Length} model endpoints are unavailable; earliest recovery at {earliest:O}.",
            attempts);
    }
}
