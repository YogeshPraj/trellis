using Microsoft.Extensions.AI;
using Trellis.Routing;

namespace Trellis.Tests;

/// <summary>Weighted and least-loaded tier ordering.</summary>
public class SelectionStrategyExtraTests
{
    private sealed class StubContext(int rotation) : ISelectionContext
    {
        public Dictionary<string, int> InFlight { get; } = [];

        public Dictionary<string, double> Latency { get; } = [];

        public int Rotation { get; } = rotation;

        public EndpointMetricsSnapshot MetricsFor(ModelEndpoint endpoint) =>
            new(Latency.GetValueOrDefault(endpoint.Name), 0, 0);

        public int InFlightFor(ModelEndpoint endpoint) => InFlight.GetValueOrDefault(endpoint.Name);
    }

    private static ModelEndpoint Endpoint(string name, int weight = 1) =>
        new(name, new FakeChatClient("ok")) { Weight = weight };

    private static string FirstPick(IEndpointSelectionStrategy strategy, IReadOnlyList<ModelEndpoint> tier, ISelectionContext context) =>
        strategy.OrderTier(tier, context).First().Name;

    [Fact]
    public void Weighted_SplitsTrafficInProportionToWeight()
    {
        var strategy = new WeightedSelectionStrategy();
        List<ModelEndpoint> tier = [Endpoint("ptu", 3), Endpoint("payg", 1)];

        Dictionary<string, int> picks = new() { ["ptu"] = 0, ["payg"] = 0 };
        for (int rotation = 0; rotation < 400; rotation++)
        {
            picks[FirstPick(strategy, tier, new StubContext(rotation))]++;
        }

        Assert.Equal(300, picks["ptu"]);
        Assert.Equal(100, picks["payg"]);
    }

    [Fact]
    public void Weighted_IsSmooth_NotClustered()
    {
        var strategy = new WeightedSelectionStrategy();
        List<ModelEndpoint> tier = [Endpoint("a", 3), Endpoint("b", 1)];

        string[] cycle = [.. Enumerable.Range(0, 4).Select(r => FirstPick(strategy, tier, new StubContext(r)))];

        // Smooth weighted round-robin interleaves rather than emitting a,a,a,b.
        Assert.Equal(3, cycle.Count(x => x == "a"));
        Assert.Single(cycle, x => x == "b");
        Assert.NotEqual(["a", "a", "a", "b"], cycle);
    }

    [Fact]
    public void Weighted_EqualWeights_DegradeToRoundRobin()
    {
        var strategy = new WeightedSelectionStrategy();
        List<ModelEndpoint> tier = [Endpoint("a"), Endpoint("b"), Endpoint("c")];

        string[] picks = [.. Enumerable.Range(0, 6).Select(r => FirstPick(strategy, tier, new StubContext(r)))];

        Assert.Equal(2, picks.Count(p => p == "a"));
        Assert.Equal(2, picks.Count(p => p == "b"));
        Assert.Equal(2, picks.Count(p => p == "c"));
    }

    [Fact]
    public void Weighted_OrdersFallbacksByCapacity()
    {
        var strategy = new WeightedSelectionStrategy();
        List<ModelEndpoint> tier = [Endpoint("small", 1), Endpoint("big", 5), Endpoint("medium", 3)];

        // Whatever wins the slot, the remaining order prefers the larger deployments.
        List<ModelEndpoint> ordered = [.. strategy.OrderTier(tier, new StubContext(0))];

        Assert.Equal(3, ordered.Count);
        List<string> fallbacks = [.. ordered.Skip(1).Select(e => e.Name)];
        Assert.Equal(fallbacks.OrderByDescending(n => tier.Single(e => e.Name == n).Weight), fallbacks);
    }

    [Fact]
    public void Weighted_RejectsAWeightBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModelEndpoint("x", new FakeChatClient("ok")) { Weight = 0 });
    }

    [Fact]
    public void LeastLoaded_PrefersTheEndpointWithFewestInFlight()
    {
        var strategy = new LeastLoadedSelectionStrategy();
        List<ModelEndpoint> tier = [Endpoint("busy"), Endpoint("idle"), Endpoint("medium")];
        var context = new StubContext(0);
        context.InFlight["busy"] = 9;
        context.InFlight["medium"] = 3;
        context.InFlight["idle"] = 0;

        Assert.Equal(["idle", "medium", "busy"], strategy.OrderTier(tier, context).Select(e => e.Name));
    }

    [Fact]
    public void LeastLoaded_SharesTrafficWhenEverythingIsIdle()
    {
        var strategy = new LeastLoadedSelectionStrategy();
        List<ModelEndpoint> tier = [Endpoint("a"), Endpoint("b"), Endpoint("c")];

        // All tied at zero: rotation must break the tie, or one endpoint takes every request.
        string[] picks = [.. Enumerable.Range(0, 3).Select(r => FirstPick(strategy, tier, new StubContext(r)))];

        Assert.Equal(3, picks.Distinct().Count());
    }

    [Fact]
    public async Task Router_TracksInFlightRequests_AndReleasesThem()
    {
        var gate = new TaskCompletionSource();
        var blocking = new GatedChatClient(gate.Task);
        var endpoint = new ModelEndpoint("slow", blocking);
        var probe = new RecordingStrategy();
        var router = new ModelRouter([endpoint], new ModelRouterOptions { SelectionStrategy = probe });

        Task<ChatResponse> first = router.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        await blocking.Started.Task;

        // A second request sees the first one still outstanding.
        Task<ChatResponse> second = router.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        await blocking.SecondStarted.Task;
        Assert.Contains(probe.Observed, load => load > 0);

        gate.SetResult();
        await Task.WhenAll(first, second);

        // Once both have completed, nothing is left in flight.
        await router.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal(0, probe.Observed[^1]);
    }

    [Fact]
    public async Task Router_ReleasesInFlight_WhenTheCallFails()
    {
        var failing = new ThrowingChatClient();
        var probe = new RecordingStrategy();
        var router = new ModelRouter(
            [new ModelEndpoint("broken", failing), new ModelEndpoint("ok", new FakeChatClient("fine"))],
            new ModelRouterOptions { SelectionStrategy = probe });

        await router.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        await router.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        // A failed attempt must not leak a permanent "in flight" against the endpoint.
        Assert.Equal(0, probe.Observed[^1]);
    }

    /// <summary>Records the in-flight count the router reports at selection time.</summary>
    private sealed class RecordingStrategy : IEndpointSelectionStrategy
    {
        public List<int> Observed { get; } = [];

        public IEnumerable<ModelEndpoint> OrderTier(IReadOnlyList<ModelEndpoint> tier, ISelectionContext context)
        {
            lock (Observed)
            {
                Observed.Add(tier.Sum(context.InFlightFor));
            }
            return tier;
        }
    }

    private sealed class GatedChatClient(Task gate) : IChatClient
    {
        private int _calls;

        public TaskCompletionSource Started { get; } = new();

        public TaskCompletionSource SecondStarted { get; } = new();

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Started.TrySetResult();
            }
            else
            {
                SecondStarted.TrySetResult();
            }
            await gate;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("429 Too Many Requests");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
