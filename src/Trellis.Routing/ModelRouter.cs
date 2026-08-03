using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Trellis.Routing;

/// <summary>
/// An <see cref="IChatClient"/> that routes across multiple model deployments by priority
/// with circuit-breaker failover and capability awareness. When an endpoint fails with a
/// transient error (rate limit, exhausted quota, outage) it is tripped: taken out of rotation
/// for an exponentially growing cooldown, so subsequent requests go straight to the next
/// deployment with no added latency. After the cooldown, the next request retries the
/// endpoint and restores it on success.
///
/// Endpoints declare <see cref="ModelCapabilities"/>; requests that need tools, vision, JSON
/// output, or a large context only consider endpoints that support them.
///
/// Conversation state stays canonical on the client: callers always pass the full message
/// history (set <see cref="ChatOptions.ConversationId"/> to your own logical id). For
/// endpoints with <see cref="ModelFeatures.ServerConversationState"/> the router transparently
/// sends only the unsynced tail plus the provider's conversation id; on failover to a
/// stateless endpoint it replays the full history, so no context is ever lost.
/// </summary>
public sealed class ModelRouter : IChatClient
{
    private sealed class EndpointState(ModelEndpoint endpoint)
    {
        public ModelEndpoint Endpoint { get; } = endpoint;
        public readonly object Lock = new();
        public int ConsecutiveFailures;
        public DateTimeOffset UnavailableUntil = DateTimeOffset.MinValue;
        public bool WasTripped;
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

    private readonly EndpointState[] _states;
    private readonly ModelRouterOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (string ProviderId, int SyncedCount)> _conversationSync = new();
    private int _roundRobin = -1;

    public ModelRouter(IReadOnlyList<ModelEndpoint> endpoints, ModelRouterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count == 0)
        {
            throw new ArgumentException("At least one endpoint is required.", nameof(endpoints));
        }
        _states = [.. endpoints.OrderBy(e => e.Priority).Select(e => new EndpointState(e))];
        _options = options ?? new ModelRouterOptions();
        _time = _options.TimeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Default transient-error classifier: trips on anything that looks like a rate limit,
    /// exhausted quota, timeout, or server-side outage; propagates everything else.
    /// </summary>
    public static bool DefaultShouldTrip(Exception exception)
    {
        if (exception is HttpRequestException or TimeoutException or TaskCanceledException)
        {
            return true;
        }

        string text = exception.ToString();
        return text.Contains("429", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ratelimit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
            || text.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || text.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            || text.Contains("500", StringComparison.Ordinal)
            || text.Contains("502", StringComparison.Ordinal)
            || text.Contains("503", StringComparison.Ordinal);
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

        foreach (EndpointState state in SelectionOrder(requirements))
        {
            cancellationToken.ThrowIfCancellationRequested();
            (IList<ChatMessage> send, ChatOptions? sendOptions) = PrepareForEndpoint(state, full, options, streaming: false);
            try
            {
                ChatResponse response = await state.Endpoint.Client
                    .GetResponseAsync(send, sendOptions, cancellationToken)
                    .ConfigureAwait(false);
                MarkSuccess(state);
                RecordConversationSync(state, full, options, response);
                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (_options.ShouldTrip(ex))
            {
                Trip(state, ex);
                attempts.Add(ex);
            }
        }

        throw AllUnavailable(attempts);
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

        foreach (EndpointState state in SelectionOrder(requirements))
        {
            cancellationToken.ThrowIfCancellationRequested();
            (IList<ChatMessage> send, ChatOptions? sendOptions) = PrepareForEndpoint(state, full, options, streaming: true);

            // Fail over only until the first token arrives; after that the stream is committed.
            IAsyncEnumerator<ChatResponseUpdate>? stream = null;
            bool hasFirst;
            ChatResponseUpdate? first = null;
            try
            {
                stream = state.Endpoint.Client
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
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                throw;
            }
            catch (Exception ex) when (_options.ShouldTrip(ex))
            {
                Trip(state, ex);
                attempts.Add(ex);
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                continue;
            }

            MarkSuccess(state);
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
                await stream!.DisposeAsync().ConfigureAwait(false);
            }
            yield break;
        }

        throw AllUnavailable(attempts);
    }

    private void ThrowIfNoneCompatible(Requirements requirements)
    {
        if (!_states.Any(s => IsCompatible(s.Endpoint.Capabilities, requirements)))
        {
            throw new NoCompatibleModelException($"No registered endpoint supports this request ({requirements}).");
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType?.IsInstanceOfType(this) == true ? this : null;

    /// <summary>The router does not own its endpoints' clients; dispose those yourself.</summary>
    public void Dispose()
    {
    }

    /// <summary>
    /// Compatible + available endpoints first (priority order, round-robin within a tier).
    /// When everything compatible is cooling down, either degrade to the soonest-recovering
    /// endpoint or yield nothing, per <see cref="ModelRouterOptions.AllTrippedBehavior"/>.
    /// </summary>
    private IEnumerable<EndpointState> SelectionOrder(Requirements requirements)
    {
        DateTimeOffset now = _time.GetUtcNow();
        List<EndpointState> available = [];
        List<EndpointState> coolingDown = [];
        foreach (EndpointState state in _states)
        {
            if (!IsCompatible(state.Endpoint.Capabilities, requirements))
            {
                continue;
            }
            (state.UnavailableUntil <= now ? available : coolingDown).Add(state);
        }

        int rotation = Interlocked.Increment(ref _roundRobin);
        IEnumerable<EndpointState> ordered = available
            .GroupBy(s => s.Endpoint.Priority)
            .OrderBy(g => g.Key)
            .SelectMany(g =>
            {
                List<EndpointState> tier = [.. g];
                int offset = tier.Count > 1 ? Math.Abs(rotation % tier.Count) : 0;
                return tier.Skip(offset).Concat(tier.Take(offset));
            });

        foreach (EndpointState state in ordered)
        {
            yield return state;
        }

        if (available.Count == 0 && _options.AllTrippedBehavior == AllTrippedBehavior.TryAnyway)
        {
            foreach (EndpointState state in coolingDown.OrderBy(s => s.UnavailableUntil))
            {
                yield return state;
            }
        }
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

    /// <summary>
    /// Adapts the request to the endpoint's conversation model. Callers always supply the
    /// canonical full history; endpoints with server-side state get only the unsynced tail
    /// plus their provider conversation id, everyone else gets the full history. Streaming
    /// calls always replay full history (sync bookkeeping for streams is not reliable) and
    /// invalidate the endpoint's sync so the next call re-establishes it.
    /// </summary>
    private (IList<ChatMessage> Messages, ChatOptions? Options) PrepareForEndpoint(
        EndpointState state,
        IList<ChatMessage> full,
        ChatOptions? options,
        bool streaming)
    {
        if (options?.ConversationId is not string logicalId)
        {
            return (full, options);
        }

        bool serverState = state.Endpoint.Capabilities.Supports(ModelFeatures.ServerConversationState);
        string key = SyncKey(logicalId, state.Endpoint.Name);

        if (!serverState || streaming)
        {
            if (serverState)
            {
                _conversationSync.TryRemove(key, out _);
            }
            // The logical id is ours, not the provider's — never leak it to a client.
            ChatOptions stripped = options.Clone();
            stripped.ConversationId = null;
            return (full, stripped);
        }

        if (_conversationSync.TryGetValue(key, out (string ProviderId, int SyncedCount) sync)
            && sync.SyncedCount <= full.Count)
        {
            ChatOptions delta = options.Clone();
            delta.ConversationId = sync.ProviderId;
            return ([.. full.Skip(sync.SyncedCount)], delta);
        }

        ChatOptions fresh = options.Clone();
        fresh.ConversationId = null;
        return (full, fresh);
    }

    private void RecordConversationSync(
        EndpointState state,
        IList<ChatMessage> full,
        ChatOptions? options,
        ChatResponse response)
    {
        if (options?.ConversationId is not string logicalId
            || !state.Endpoint.Capabilities.Supports(ModelFeatures.ServerConversationState)
            || response.ConversationId is not string providerId)
        {
            return;
        }
        // The server now knows the full input plus the messages it just generated.
        _conversationSync[SyncKey(logicalId, state.Endpoint.Name)] = (providerId, full.Count + response.Messages.Count);
    }

    private static string SyncKey(string logicalId, string endpointName) => logicalId + "|" + endpointName;

    private void Trip(EndpointState state, Exception cause)
    {
        DateTimeOffset until;
        lock (state.Lock)
        {
            state.ConsecutiveFailures++;
            double factor = Math.Pow(2, Math.Min(state.ConsecutiveFailures - 1, 10));
            TimeSpan cooldown = TimeSpan.FromTicks(Math.Min(
                (long)(_options.BaseCooldown.Ticks * factor),
                _options.MaxCooldown.Ticks));
            until = _time.GetUtcNow() + cooldown;
            state.UnavailableUntil = until;
            state.WasTripped = true;
        }
        _options.OnEndpointTripped?.Invoke(state.Endpoint, cause, until);
    }

    private void MarkSuccess(EndpointState state)
    {
        bool recovered;
        lock (state.Lock)
        {
            recovered = state.WasTripped;
            state.ConsecutiveFailures = 0;
            state.UnavailableUntil = DateTimeOffset.MinValue;
            state.WasTripped = false;
        }
        if (recovered)
        {
            _options.OnEndpointRecovered?.Invoke(state.Endpoint);
        }
    }

    private AllModelsUnavailableException AllUnavailable(List<Exception> attempts)
    {
        DateTimeOffset earliest = _states.Min(s => s.UnavailableUntil);
        return new AllModelsUnavailableException(
            $"All {_states.Length} model endpoints are unavailable; earliest recovery at {earliest:O}.",
            attempts);
    }
}
