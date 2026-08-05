using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Trellis.Routing;
using Trellis.State;
using Trellis.State.Redis;

namespace Trellis.Tests;

public class InMemorySharedStateStoreTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task SetGetRemove_RoundTrips()
    {
        var store = new InMemorySharedStateStore();

        await store.SetAsync("k", "v");
        Assert.Equal("v", await store.GetAsync("k"));

        await store.RemoveAsync("k");
        Assert.Null(await store.GetAsync("k"));
    }

    [Fact]
    public async Task ConcurrentAppends_LoseNothing()
    {
        var store = new InMemorySharedStateStore();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i =>
            Task.Run(() => store.AppendAsync("list", $"item-{i}").AsTask())));

        Assert.Equal(200, (await store.GetListAsync("list")).Count);
    }

    [Fact]
    public async Task ConcurrentIncrements_LoseNothing()
    {
        var store = new InMemorySharedStateStore();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ =>
            Task.Run(() => store.IncrementAsync("counter").AsTask())));

        Assert.Equal("200", await store.GetAsync("counter"));
    }

    [Fact]
    public async Task Entry_ExpiresAfterTtl()
    {
        var time = new FakeTime();
        var store = new InMemorySharedStateStore(time);

        await store.SetAsync("k", "v", TimeSpan.FromSeconds(30));
        Assert.Equal("v", await store.GetAsync("k"));

        time.Now += TimeSpan.FromSeconds(31);
        Assert.Null(await store.GetAsync("k"));
    }
}

public class DistributedCacheBridgeTests
{
    private static DistributedCacheSharedStateStore NewStore() =>
        new(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

    [Fact]
    public async Task BridgesAnyIDistributedCache()
    {
        DistributedCacheSharedStateStore store = NewStore();

        await store.SetAsync("k", "v");
        Assert.Equal("v", await store.GetAsync("k"));

        await store.RemoveAsync("k");
        Assert.Null(await store.GetAsync("k"));
    }
}

public class RedisSharedStateStoreTests
{
    private static (RedisSharedStateStore Store, IDatabase Db) NewStore()
    {
        IConnectionMultiplexer connection = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return (new RedisSharedStateStore(connection, keyPrefix: "trellis:"), db);
    }

    [Fact]
    public async Task Get_ReadsPrefixedKey_AndMapsNullToNull()
    {
        (RedisSharedStateStore store, IDatabase db) = NewStore();
        db.StringGetAsync("trellis:health:primary", Arg.Any<CommandFlags>())
            .Returns(Task.FromResult((RedisValue)"json-blob"));
        db.StringGetAsync("trellis:missing", Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisValue.Null));

        Assert.Equal("json-blob", await store.GetAsync("health:primary"));
        Assert.Null(await store.GetAsync("missing"));
    }

    [Fact]
    public async Task Set_WritesPrefixedKey_WithTtl()
    {
        (RedisSharedStateStore store, IDatabase db) = NewStore();

        await store.SetAsync("health:primary", "json-blob", TimeSpan.FromMinutes(20));

        await db.Received(1).StringSetAsync(
            (RedisKey)"trellis:health:primary",
            (RedisValue)"json-blob",
            (Expiration)TimeSpan.FromMinutes(20));
    }

    [Fact]
    public async Task Remove_DeletesPrefixedKey()
    {
        (RedisSharedStateStore store, IDatabase db) = NewStore();

        await store.RemoveAsync("health:primary");

        await db.Received(1).KeyDeleteAsync((RedisKey)"trellis:health:primary", Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task CompareAndSwap_RunsServerSide_AndPassesExpectedAndNewValues()
    {
        (RedisSharedStateStore store, IDatabase db) = NewStore();
        db.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)1)));

        bool swapped = await store.TrySetIfUnchangedAsync("conversation:c1", "old", "new", TimeSpan.FromMinutes(5));

        Assert.True(swapped);
        await db.Received(1).ScriptEvaluateAsync(
            Arg.Is<string>(script => script!.Contains("redis.call('GET'", StringComparison.Ordinal)),
            Arg.Is<RedisKey[]>(keys => keys!.Single() == (RedisKey)"trellis:conversation:c1"),
            Arg.Is<RedisValue[]>(values =>
                values![0] == (RedisValue)"old"
                && values[1] == (RedisValue)"new"
                && values[2] == (RedisValue)"0"
                && values[3] == (RedisValue)"300000"),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task CompareAndSwap_SignalsExpectAbsent_AndReportsRefusal()
    {
        (RedisSharedStateStore store, IDatabase db) = NewStore();
        db.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)0)));

        bool swapped = await store.TrySetIfUnchangedAsync("conversation:c1", expectedValue: null, "new");

        Assert.False(swapped);
        await db.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            // ARGV[3] = "1" means "the key must not exist"; ARGV[4] = "" means no TTL.
            Arg.Is<RedisValue[]>(values => values![2] == (RedisValue)"1" && values[3] == (RedisValue)string.Empty),
            Arg.Any<CommandFlags>());
    }
}

public class SharedHealthStoreFleetTests
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

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new HttpRequestException("429 Too Many Requests");
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

    [Fact]
    public async Task HealthRoundTrips_ThroughSharedStateStore()
    {
        var store = new SharedStateEndpointHealthStore(new InMemorySharedStateStore());
        var until = new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);

        Assert.Equal(1, await store.RecordFailureAsync("primary"));
        Assert.Equal(2, await store.RecordFailureAsync("primary"));
        Assert.Equal(3, await store.RecordFailureAsync("primary"));
        await store.SetCooldownAsync("primary", until);

        Assert.Equal(new EndpointHealth(3, until, true), await store.GetAsync("primary"));
        Assert.Equal(EndpointHealth.Healthy, await store.GetAsync("never-seen"));

        await store.ResetAsync("primary");
        Assert.Equal(EndpointHealth.Healthy, await store.GetAsync("primary"));
    }

    [Fact]
    public async Task Fleet_SharesTripsAcrossRouterInstances_ViaSharedStore()
    {
        var time = new FakeTime();
        // One shared backend (stand-in for Redis), two "app instances" each with their own
        // router built over their own adapter — only the backend is shared.
        var backend = new InMemorySharedStateStore(time);
        var primary = new ScriptedClient("primary") { FailuresRemaining = 1 };
        var fallback = new ScriptedClient("fallback");

        ModelRouter NewInstance() => new(
            [new("primary", primary, 0), new("fallback", fallback, 1)],
            new ModelRouterOptions
            {
                TimeProvider = time,
                HealthStore = new SharedStateEndpointHealthStore(backend),
            });

        static async Task<string> Ask(ModelRouter router) =>
            (await router.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")])).Text;

        // Instance 1 pays the failover cost and trips the primary in the shared backend...
        Assert.Equal("fallback", await Ask(NewInstance()));
        Assert.Equal(1, primary.Calls);

        // ...instance 2 sees the trip and never touches the primary.
        Assert.Equal("fallback", await Ask(NewInstance()));
        Assert.Equal(1, primary.Calls);

        // After the cooldown, any instance half-opens it and recovery propagates fleet-wide.
        time.Now += TimeSpan.FromSeconds(31);
        Assert.Equal("primary", await Ask(NewInstance()));
    }
}
