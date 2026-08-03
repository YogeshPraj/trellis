using System.Net;
using Microsoft.Extensions.AI;
using Trellis.Routing;

namespace Trellis.Tests;

public class FailureClassifierTests
{
    private readonly DefaultFailureClassifier _classifier = new();

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, FailureKind.RateLimit)]
    [InlineData(HttpStatusCode.PaymentRequired, FailureKind.QuotaExhausted)]
    [InlineData(HttpStatusCode.RequestTimeout, FailureKind.Timeout)]
    [InlineData(HttpStatusCode.InternalServerError, FailureKind.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, FailureKind.ServerError)]
    public void HttpStatusCodes_ClassifyTyped(HttpStatusCode status, FailureKind expected)
    {
        var ex = new HttpRequestException("boom", null, status);
        Assert.Equal(expected, _classifier.Classify(ex).Kind);
    }

    [Theory]
    [InlineData("This model's maximum context length is 8192 tokens", FailureKind.ContextWindowExceeded)]
    [InlineData("The response was filtered due to content_filter", FailureKind.ContentPolicy)]
    [InlineData("You exceeded your current quota, please check billing", FailureKind.QuotaExhausted)]
    [InlineData("429 Too Many Requests", FailureKind.RateLimit)]
    [InlineData("The upstream server is overloaded", FailureKind.ServerError)]
    [InlineData("Value cannot be negative", FailureKind.Unknown)]
    public void MessageHeuristics_ClassifyByText(string message, FailureKind expected)
    {
        Assert.Equal(expected, _classifier.Classify(new InvalidOperationException(message)).Kind);
    }

    [Fact]
    public void RetryAfter_IsReadFromExceptionData()
    {
        var ex = new HttpRequestException("429", null, HttpStatusCode.TooManyRequests);
        ex.Data["RetryAfter"] = 17;

        FailureClassification classification = _classifier.Classify(ex);

        Assert.Equal(TimeSpan.FromSeconds(17), classification.RetryAfter);
    }

    [Fact]
    public void DefaultPolicy_MapsKindsToExpectedActions()
    {
        var policy = new DefaultFailurePolicy();

        Assert.Equal(FailureAction.FailoverAndTrip, policy.Decide(new(FailureKind.RateLimit)));
        Assert.Equal(FailureAction.FailoverOnly, policy.Decide(new(FailureKind.ContextWindowExceeded)));
        Assert.Equal(FailureAction.FailoverOnly, policy.Decide(new(FailureKind.ContentPolicy)));
        Assert.Equal(FailureAction.Propagate, policy.Decide(new(FailureKind.Unknown)));
    }

    [Fact]
    public void Policy_Overrides_AreApplied()
    {
        var policy = new DefaultFailurePolicy(new Dictionary<FailureKind, FailureAction>
        {
            [FailureKind.ContentPolicy] = FailureAction.Propagate,
        });

        Assert.Equal(FailureAction.Propagate, policy.Decide(new(FailureKind.ContentPolicy)));
        Assert.Equal(FailureAction.FailoverAndTrip, policy.Decide(new(FailureKind.RateLimit)));
    }
}

public class FailurePolicyRoutingTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ScriptedClient(string name) : IChatClient
    {
        public int Calls;
        public int FailuresRemaining;
        public Exception Failure { get; set; } = new HttpRequestException("429 Too Many Requests");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw Failure;
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, name)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static Task<string> AskAsync(IChatClient router) =>
        Ask(router);

    private static async Task<string> Ask(IChatClient router) =>
        (await router.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")])).Text;

    [Fact]
    public async Task ContextWindowError_FailsOverWithoutTrippingTheEndpoint()
    {
        var primary = new ScriptedClient("primary")
        {
            FailuresRemaining = 1,
            Failure = new InvalidOperationException("This model's maximum context length is 8192 tokens"),
        };
        var fallback = new ScriptedClient("fallback");
        var router = new ModelRouter(
            [new("primary", primary, 0), new("fallback", fallback, 1)],
            new ModelRouterOptions { TimeProvider = new FakeTime() });

        // The oversized request falls over to the fallback...
        Assert.Equal("fallback", await AskAsync(router));

        // ...but the primary was NOT tripped: the very next request goes to it again.
        Assert.Equal("primary", await AskAsync(router));
        Assert.Equal(2, primary.Calls);
    }

    [Fact]
    public async Task RetryAfter_OverridesExponentialCooldown()
    {
        var time = new FakeTime();
        var failure = new HttpRequestException("429", null, System.Net.HttpStatusCode.TooManyRequests);
        failure.Data["RetryAfter"] = 5;
        var primary = new ScriptedClient("primary") { FailuresRemaining = 1, Failure = failure };
        var fallback = new ScriptedClient("fallback");
        var router = new ModelRouter(
            [new("primary", primary, 0), new("fallback", fallback, 1)],
            new ModelRouterOptions { TimeProvider = time, BaseCooldown = TimeSpan.FromMinutes(5) });

        Assert.Equal("fallback", await AskAsync(router));

        // 6 seconds later the provider-requested backoff has passed — despite the
        // 5-minute BaseCooldown — and the primary is retried.
        time.Now += TimeSpan.FromSeconds(6);
        Assert.Equal("primary", await AskAsync(router));
    }

    [Fact]
    public async Task SharedHealthStore_PropagatesTripsAcrossRouterInstances()
    {
        var time = new FakeTime();
        var store = new InMemoryEndpointHealthStore();
        var primary = new ScriptedClient("primary") { FailuresRemaining = 1 };
        var fallback = new ScriptedClient("fallback");

        ModelRouter NewRouter() => new(
            [new("primary", primary, 0), new("fallback", fallback, 1)],
            new ModelRouterOptions { TimeProvider = time, HealthStore = store });

        // "Instance 1" trips the primary...
        Assert.Equal("fallback", await AskAsync(NewRouter()));
        Assert.Equal(1, primary.Calls);

        // ..."instance 2" (fresh router, same store) skips it without a single call.
        Assert.Equal("fallback", await AskAsync(NewRouter()));
        Assert.Equal(1, primary.Calls);
    }
}

public class SelectionStrategyTests
{
    private sealed class FakeContext(Dictionary<string, EndpointMetricsSnapshot> metrics) : ISelectionContext
    {
        public int Rotation => 0;

        public EndpointMetricsSnapshot MetricsFor(ModelEndpoint endpoint) =>
            metrics.GetValueOrDefault(endpoint.Name, new EndpointMetricsSnapshot(0, 0, 0));
    }

    private static ModelEndpoint Endpoint(string name, double? cost = null) =>
        new(name, new FakeChatClient("x")) { CostPerMillionTokens = cost };

    [Fact]
    public void LowestLatency_PrefersFasterEndpoint_AndUnmeasuredFirst()
    {
        ModelEndpoint slow = Endpoint("slow");
        ModelEndpoint fast = Endpoint("fast");
        ModelEndpoint fresh = Endpoint("fresh");
        var context = new FakeContext(new()
        {
            ["slow"] = new EndpointMetricsSnapshot(900, 10, 0),
            ["fast"] = new EndpointMetricsSnapshot(120, 10, 0),
        });

        var order = new LowestLatencySelectionStrategy().OrderTier([slow, fast, fresh], context).ToList();

        Assert.Equal(["fresh", "fast", "slow"], order.Select(e => e.Name));
    }

    [Fact]
    public void LowestCost_PrefersCheaper_UndeclaredCostLast()
    {
        ModelEndpoint pricey = Endpoint("pricey", cost: 15.0);
        ModelEndpoint cheap = Endpoint("cheap", cost: 0.5);
        ModelEndpoint unknown = Endpoint("unknown");
        var context = new FakeContext([]);

        var order = new LowestCostSelectionStrategy().OrderTier([pricey, cheap, unknown], context).ToList();

        Assert.Equal(["cheap", "pricey", "unknown"], order.Select(e => e.Name));
    }

    [Fact]
    public void RoundRobin_RotatesByRequestCounter()
    {
        ModelEndpoint a = Endpoint("a");
        ModelEndpoint b = Endpoint("b");
        var strategy = new RoundRobinSelectionStrategy();

        var context = new FakeContext([]);
        Assert.Equal(["a", "b"], strategy.OrderTier([a, b], context).Select(e => e.Name));
    }
}
