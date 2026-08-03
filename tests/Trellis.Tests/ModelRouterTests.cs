using Microsoft.Extensions.AI;
using Trellis.Routing;

namespace Trellis.Tests;

public class ModelRouterTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Succeeds with its name; throws while <see cref="FailuresRemaining"/> &gt; 0.</summary>
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
            Calls++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw Failure;
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, name);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "!");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static async Task<string> AskAsync(IChatClient router) =>
        (await router.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")])).Text;

    [Fact]
    public async Task HealthyPrimary_ServesEveryRequest()
    {
        var primary = new ScriptedClient("primary");
        var fallback = new ScriptedClient("fallback");
        var router = new ModelRouter([new("primary", primary, 0), new("fallback", fallback, 1)]);

        Assert.Equal("primary", await AskAsync(router));
        Assert.Equal("primary", await AskAsync(router));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task TrippedPrimary_IsSkippedWithoutBeingCalled()
    {
        var primary = new ScriptedClient("primary") { FailuresRemaining = 1 };
        var fallback = new ScriptedClient("fallback");
        var router = new ModelRouter(
            [new("primary", primary, 0), new("fallback", fallback, 1)],
            new ModelRouterOptions { TimeProvider = new FakeTime() });

        // First request pays the failover cost once...
        Assert.Equal("fallback", await AskAsync(router));
        Assert.Equal(1, primary.Calls);

        // ...but subsequent requests skip the tripped primary entirely: no friction.
        Assert.Equal("fallback", await AskAsync(router));
        Assert.Equal("fallback", await AskAsync(router));
        Assert.Equal(1, primary.Calls);
        Assert.Equal(3, fallback.Calls);
    }

    [Fact]
    public async Task AfterCooldown_PrimaryIsRetriedAndRestored()
    {
        var time = new FakeTime();
        var primary = new ScriptedClient("primary") { FailuresRemaining = 1 };
        var fallback = new ScriptedClient("fallback");
        ModelEndpoint? recovered = null;
        var router = new ModelRouter(
            [new("primary", primary, 0), new("fallback", fallback, 1)],
            new ModelRouterOptions
            {
                TimeProvider = time,
                BaseCooldown = TimeSpan.FromSeconds(30),
                OnEndpointRecovered = e => recovered = e,
            });

        Assert.Equal("fallback", await AskAsync(router));

        // Cooldown elapses → the next request half-opens the primary, which now succeeds.
        time.Now += TimeSpan.FromSeconds(31);
        Assert.Equal("primary", await AskAsync(router));
        Assert.Equal("primary", recovered?.Name);

        // Fully restored: it keeps serving.
        Assert.Equal("primary", await AskAsync(router));
    }

    [Fact]
    public async Task RepeatedFailures_BackOffExponentially()
    {
        var time = new FakeTime();
        var primary = new ScriptedClient("primary") { FailuresRemaining = 2 };
        var fallback = new ScriptedClient("fallback");
        var router = new ModelRouter(
            [new("primary", primary, 0), new("fallback", fallback, 1)],
            new ModelRouterOptions { TimeProvider = time, BaseCooldown = TimeSpan.FromSeconds(30) });

        await AskAsync(router);                      // failure #1 → 30s cooldown
        time.Now += TimeSpan.FromSeconds(31);
        await AskAsync(router);                      // failure #2 → 60s cooldown
        Assert.Equal(2, primary.Calls);

        time.Now += TimeSpan.FromSeconds(31);        // only 31s later: still cooling down
        await AskAsync(router);
        Assert.Equal(2, primary.Calls);

        time.Now += TimeSpan.FromSeconds(30);        // 61s after failure #2: retried
        Assert.Equal("primary", await AskAsync(router));
    }

    [Fact]
    public async Task NonTransientError_PropagatesWithoutFailover()
    {
        var primary = new ScriptedClient("primary")
        {
            FailuresRemaining = 1,
            Failure = new ArgumentException("prompt too long for every model"),
        };
        var fallback = new ScriptedClient("fallback");
        var router = new ModelRouter([new("primary", primary, 0), new("fallback", fallback, 1)]);

        await Assert.ThrowsAsync<ArgumentException>(() => AskAsync(router));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task AllTripped_Throw_FailsFastWithDetails()
    {
        var a = new ScriptedClient("a") { FailuresRemaining = 99 };
        var b = new ScriptedClient("b") { FailuresRemaining = 99 };
        var router = new ModelRouter(
            [new("a", a, 0), new("b", b, 1)],
            new ModelRouterOptions { TimeProvider = new FakeTime(), AllTrippedBehavior = AllTrippedBehavior.Throw });

        await Assert.ThrowsAsync<AllModelsUnavailableException>(() => AskAsync(router));

        // Both are cooling down now; fail-fast without calling either again.
        var ex = await Assert.ThrowsAsync<AllModelsUnavailableException>(() => AskAsync(router));
        Assert.Equal(1, a.Calls);
        Assert.Equal(1, b.Calls);
        Assert.Empty(ex.Attempts);
    }

    [Fact]
    public async Task AllTripped_TryAnyway_DegradesGracefully()
    {
        var time = new FakeTime();
        var a = new ScriptedClient("a") { FailuresRemaining = 1 };
        var b = new ScriptedClient("b") { FailuresRemaining = 1 };
        var router = new ModelRouter(
            [new("a", a, 0), new("b", b, 1)],
            new ModelRouterOptions { TimeProvider = time });

        // The first request exhausts both endpoints (each fails once and trips).
        await Assert.ThrowsAsync<AllModelsUnavailableException>(() => AskAsync(router));

        // With TryAnyway the next request still attempts the soonest-recovering endpoint
        // instead of failing fast — and it succeeds now.
        Assert.Equal("a", await AskAsync(router));
    }

    [Fact]
    public async Task SamePriority_EndpointsShareLoadRoundRobin()
    {
        var a = new ScriptedClient("a");
        var b = new ScriptedClient("b");
        var router = new ModelRouter([new("a", a, 0), new("b", b, 0)]);

        for (int i = 0; i < 6; i++)
        {
            await AskAsync(router);
        }

        Assert.Equal(3, a.Calls);
        Assert.Equal(3, b.Calls);
    }

    [Fact]
    public async Task Streaming_FailsOverBeforeFirstToken()
    {
        var primary = new ScriptedClient("primary") { FailuresRemaining = 1 };
        var fallback = new ScriptedClient("fallback");
        var router = new ModelRouter(
            [new("primary", primary, 0), new("fallback", fallback, 1)],
            new ModelRouterOptions { TimeProvider = new FakeTime() });

        List<string> chunks = [];
        await foreach (ChatResponseUpdate update in router.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            chunks.Add(update.Text);
        }

        Assert.Equal(["fallback", "!"], chunks);
    }

    [Fact]
    public async Task Router_PlugsStraightIntoAnAgent()
    {
        var primary = new ScriptedClient("primary") { FailuresRemaining = 1 };
        var fallback = new ScriptedClient("fallback");
        var router = new ModelRouter(
            [new("primary", primary, 0), new("fallback", fallback, 1)],
            new ModelRouterOptions { TimeProvider = new FakeTime() });

        var agent = new Agent(router, instructions: "Be brief.");

        Assert.Equal("fallback", (await agent.RunAsync("hi")).Output);
    }
}
