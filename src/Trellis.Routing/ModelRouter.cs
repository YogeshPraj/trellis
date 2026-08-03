using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Trellis.Routing;

/// <summary>
/// An <see cref="IChatClient"/> that routes across multiple model deployments by priority
/// with circuit-breaker failover. When an endpoint fails with a transient error (rate limit,
/// exhausted quota, outage) it is tripped: taken out of rotation for an exponentially growing
/// cooldown, so subsequent requests go straight to the next deployment with no added latency.
/// After the cooldown, the next request retries the endpoint and restores it on success.
/// Because this is just an IChatClient, it plugs into any agent or graph unchanged.
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

    private readonly EndpointState[] _states;
    private readonly ModelRouterOptions _options;
    private readonly TimeProvider _time;
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
        IList<ChatMessage> stable = messages as IList<ChatMessage> ?? [.. messages];
        List<Exception> attempts = [];

        foreach (EndpointState state in SelectionOrder())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ChatResponse response = await state.Endpoint.Client
                    .GetResponseAsync(stable, options, cancellationToken)
                    .ConfigureAwait(false);
                MarkSuccess(state);
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
        IList<ChatMessage> stable = messages as IList<ChatMessage> ?? [.. messages];
        List<Exception> attempts = [];

        foreach (EndpointState state in SelectionOrder())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Fail over only until the first token arrives; after that the stream is committed.
            IAsyncEnumerator<ChatResponseUpdate>? stream = null;
            bool hasFirst;
            ChatResponseUpdate? first = null;
            try
            {
                stream = state.Endpoint.Client
                    .GetStreamingResponseAsync(stable, options, cancellationToken)
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

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType?.IsInstanceOfType(this) == true ? this : null;

    /// <summary>The router does not own its endpoints' clients; dispose those yourself.</summary>
    public void Dispose()
    {
    }

    /// <summary>
    /// Available endpoints first (priority order, round-robin within a tier). When everything
    /// is cooling down, either degrade to the soonest-recovering endpoint or yield nothing,
    /// per <see cref="ModelRouterOptions.AllTrippedBehavior"/>.
    /// </summary>
    private IEnumerable<EndpointState> SelectionOrder()
    {
        DateTimeOffset now = _time.GetUtcNow();
        List<EndpointState> available = [];
        List<EndpointState> coolingDown = [];
        foreach (EndpointState state in _states)
        {
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
