using Microsoft.Extensions.AI;
using Trellis.State;

namespace Trellis.Tests;

public class TieredConversationStoreTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>An in-memory store whose reads and writes can be made to fail on demand.</summary>
    private sealed class FaultyStore : IAtomicSharedStateStore
    {
        private readonly InMemorySharedStateStore _inner = new();

        public bool FailReads { get; set; }

        public bool FailWrites { get; set; }

        public bool FailRemoves { get; set; }

        public int Writes { get; private set; }

        /// <summary>Bypasses fault injection, for asserting what a tier actually holds.</summary>
        public ValueTask<string?> PeekAsync(string key) => _inner.GetAsync(key);

        public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            FailReads ? throw new InvalidOperationException("tier read down") : _inner.GetAsync(key, cancellationToken);

        public ValueTask SetAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
        {
            if (FailWrites)
            {
                throw new InvalidOperationException("tier write down");
            }
            Writes++;
            return _inner.SetAsync(key, value, timeToLive, cancellationToken);
        }

        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            FailRemoves ? throw new InvalidOperationException("tier remove down") : _inner.RemoveAsync(key, cancellationToken);

        public ValueTask<bool> TrySetIfUnchangedAsync(
            string key, string? expectedValue, string newValue, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
        {
            if (FailWrites)
            {
                throw new InvalidOperationException("tier write down");
            }
            Writes++;
            return _inner.TrySetIfUnchangedAsync(key, expectedValue, newValue, timeToLive, cancellationToken);
        }

        public ValueTask<long> IncrementAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.IncrementAsync(key, cancellationToken);

        public ValueTask<long> AppendAsync(string key, string value, CancellationToken cancellationToken = default) =>
            _inner.AppendAsync(key, value, cancellationToken);

        public ValueTask<IReadOnlyList<string>> GetListAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.GetListAsync(key, cancellationToken);
    }

    private static Conversation NewConversation(string id, string text)
    {
        var conversation = new Conversation(id);
        conversation.Add(new ChatMessage(ChatRole.User, text));
        return conversation;
    }

    [Fact]
    public async Task WritesThroughEveryTier()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        var store = new TieredConversationStore(
            new ConversationTier("redis", fast),
            new ConversationTier("cosmos", durable));

        await store.SaveAsync(NewConversation("c1", "hello"));

        Assert.NotNull(await fast.PeekAsync("conversation:c1"));
        Assert.NotNull(await durable.PeekAsync("conversation:c1"));
        Assert.Equal("cosmos", store.AuthorityName);
    }

    [Fact]
    public async Task ChainCanBeAnyLength()
    {
        var t1 = new FaultyStore();
        var t2 = new FaultyStore();
        var t3 = new FaultyStore();
        var t4 = new FaultyStore();
        var store = new TieredConversationStore(
            new ConversationTier("memory", t1),
            new ConversationTier("redis", t2),
            new ConversationTier("cosmos", t3),
            new ConversationTier("blob", t4));

        await store.SaveAsync(NewConversation("c1", "hello"));

        Assert.Equal("blob", store.AuthorityName);
        foreach (FaultyStore tier in new[] { t1, t2, t3, t4 })
        {
            Assert.NotNull(await tier.PeekAsync("conversation:c1"));
        }
        Assert.NotNull(await store.LoadAsync("c1"));
    }

    [Fact]
    public async Task ReadsPreferTheFastestTier()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        var store = new TieredConversationStore(
            new ConversationTier("redis", fast),
            new ConversationTier("cosmos", durable));
        await store.SaveAsync(NewConversation("c1", "hello"));

        durable.FailReads = true;   // never consulted when the fast tier hits

        Conversation? loaded = await store.LoadAsync("c1");

        Assert.Equal("hello", loaded!.Messages[0].Text);
    }

    [Fact]
    public async Task ReadFallsThroughOnMiss_AndBackfillsFasterTiers()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        var store = new TieredConversationStore(
            new ConversationTier("redis", fast),
            new ConversationTier("cosmos", durable));
        await store.SaveAsync(NewConversation("c1", "hello"));

        await fast.RemoveAsync("conversation:c1");   // e.g. TTL eviction
        Assert.Null(await fast.PeekAsync("conversation:c1"));

        Conversation? loaded = await store.LoadAsync("c1");

        Assert.Equal("hello", loaded!.Messages[0].Text);
        Assert.NotNull(await fast.PeekAsync("conversation:c1"));   // backfilled
    }

    [Fact]
    public async Task AuthorityOwnsTheVersionCheck()
    {
        var store = new TieredConversationStore(
            new ConversationTier("redis", new FaultyStore()),
            new ConversationTier("cosmos", new FaultyStore()));
        await store.SaveAsync(NewConversation("c1", "first"));

        Conversation a = (await store.LoadAsync("c1"))!;
        Conversation b = (await store.LoadAsync("c1"))!;
        a.Add(new ChatMessage(ChatRole.User, "from A"));
        b.Add(new ChatMessage(ChatRole.User, "from B"));

        await store.SaveAsync(a);
        var ex = await Assert.ThrowsAsync<ConversationConcurrencyException>(() => store.SaveAsync(b).AsTask());

        Assert.Equal(1, ex.ExpectedVersion);
        Assert.Equal(2, ex.ActualVersion);
        Assert.Equal("from A", (await store.LoadAsync("c1"))!.Messages[^1].Text);
    }

    [Fact]
    public async Task ReplicaFailure_DoesNotFailTheTurn_AndLeavesNoStaleEntry()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        List<string> unhealthy = [];
        var store = new TieredConversationStore(
            [new ConversationTier("redis", fast), new ConversationTier("cosmos", durable)],
            new TieredConversationStoreOptions { OnTierUnhealthy = (name, _) => unhealthy.Add(name) });

        await store.SaveAsync(NewConversation("c1", "first"));
        Conversation second = (await store.LoadAsync("c1"))!;
        second.Add(new ChatMessage(ChatRole.User, "second"));

        fast.FailWrites = true;
        await store.SaveAsync(second);      // must not throw

        Assert.Equal("redis", Assert.Single(unhealthy));
        // The stale v1 copy is gone, so the fast tier can never serve it.
        Assert.Null(await fast.PeekAsync("conversation:c1"));
        Assert.Equal(2, (await store.LoadAsync("c1"))!.Version);
    }

    [Fact]
    public async Task UnhealthyTier_IsSkipped_ThenRetriedAfterCooldown()
    {
        var time = new FakeTime();
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        List<string> recovered = [];
        var store = new TieredConversationStore(
            [new ConversationTier("redis", fast), new ConversationTier("cosmos", durable)],
            new TieredConversationStoreOptions
            {
                UnhealthyCooldown = TimeSpan.FromSeconds(30),
                OnTierRecovered = recovered.Add,
            },
            time);

        await store.SaveAsync(NewConversation("c1", "first"));

        fast.FailWrites = true;
        Conversation second = (await store.LoadAsync("c1"))!;
        second.Add(new ChatMessage(ChatRole.User, "second"));
        await store.SaveAsync(second);
        Assert.Contains("redis", store.UnhealthyTiers);

        // Still cooling down: writes skip the tier entirely.
        fast.FailWrites = false;
        int writesBefore = fast.Writes;
        Conversation third = (await store.LoadAsync("c1"))!;
        third.Add(new ChatMessage(ChatRole.User, "third"));
        await store.SaveAsync(third);
        Assert.Equal(writesBefore, fast.Writes);

        time.Now += TimeSpan.FromSeconds(31);
        Assert.Empty(store.UnhealthyTiers);
        Assert.Contains("redis", recovered);

        Conversation fourth = (await store.LoadAsync("c1"))!;
        fourth.Add(new ChatMessage(ChatRole.User, "fourth"));
        await store.SaveAsync(fourth);
        Assert.True(fast.Writes > writesBefore, "the recovered tier should receive writes again");
    }

    [Fact]
    public async Task RecoveredTier_IsNotTrustedForReadsUntilRepaired()
    {
        var time = new FakeTime();
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        var store = new TieredConversationStore(
            [new ConversationTier("redis", fast), new ConversationTier("cosmos", durable)],
            new TieredConversationStoreOptions { UnhealthyCooldown = TimeSpan.FromSeconds(30) },
            time);

        await store.SaveAsync(NewConversation("c1", "turn one"));

        // The fast tier goes fully dark: the replica write fails AND so does the cleanup, so
        // it keeps its pre-outage copy — the exact setup that produces a stale read later.
        fast.FailWrites = true;
        fast.FailRemoves = true;
        Conversation second = (await store.LoadAsync("c1"))!;
        second.Add(new ChatMessage(ChatRole.User, "turn two"));
        await store.SaveAsync(second);

        Assert.NotNull(await fast.PeekAsync("conversation:c1"));   // stale v1 still there

        // The tier comes back and its cooldown expires.
        fast.FailWrites = false;
        fast.FailRemoves = false;
        time.Now += TimeSpan.FromSeconds(31);

        Conversation? loaded = await store.LoadAsync("c1");

        // It must NOT serve the stale copy it kept through the outage.
        Assert.Equal(2, loaded!.Version);
        Assert.Equal(2, loaded.Messages.Count);
        Assert.Equal("turn two", loaded.Messages[^1].Text);
    }

    [Fact]
    public async Task RepairMode_EndsOnceTheTiersTtlHasElapsed()
    {
        var time = new FakeTime();
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        var store = new TieredConversationStore(
            [
                new ConversationTier("redis", fast, TimeToLive: TimeSpan.FromMinutes(10)),
                new ConversationTier("cosmos", durable),
            ],
            new TieredConversationStoreOptions { UnhealthyCooldown = TimeSpan.FromSeconds(30) },
            time);

        await store.SaveAsync(NewConversation("c1", "turn one"));

        // Fast tier goes dark and keeps its stale copy through the outage.
        fast.FailWrites = true;
        fast.FailRemoves = true;
        Conversation second = (await store.LoadAsync("c1"))!;
        second.Add(new ChatMessage(ChatRole.User, "turn two"));
        await store.SaveAsync(second);

        fast.FailWrites = false;
        fast.FailRemoves = false;
        time.Now += TimeSpan.FromSeconds(31);       // cooldown expires → repair mode starts

        // Still inside the TTL window: an untouched conversation must not be read from it.
        await fast.SetAsync("conversation:untouched", """{"id":"untouched","version":99,"messages":[]}""");
        Assert.Null(await store.LoadAsync("untouched"));

        // Past the TTL, nothing written before the outage can still exist, so the tier is
        // trusted wholesale again.
        time.Now += TimeSpan.FromMinutes(11);
        await fast.SetAsync("conversation:fresh", """{"id":"fresh","version":7,"messages":[]}""");

        Conversation? served = await store.LoadAsync("fresh");

        Assert.NotNull(served);
        Assert.Equal(7, served.Version);
    }

    [Fact]
    public async Task AuthorityDown_FailsTheSaveByDefault()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        var store = new TieredConversationStore(
            new ConversationTier("redis", fast),
            new ConversationTier("cosmos", durable));

        durable.FailWrites = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(NewConversation("c1", "hi")).AsTask());

        // Nothing was written anywhere: the conversation cannot fork.
        Assert.Null(await fast.PeekAsync("conversation:c1"));
    }

    [Fact]
    public async Task AuthorityDown_CanPromoteTheHealthiestTier_WhenOptedIn()
    {
        var time = new FakeTime();
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        var store = new TieredConversationStore(
            [new ConversationTier("redis", fast), new ConversationTier("cosmos", durable)],
            new TieredConversationStoreOptions
            {
                OnAuthorityUnavailable = AuthorityUnavailableBehavior.PromoteHealthiest,
                UnhealthyCooldown = TimeSpan.FromSeconds(30),
            },
            time);

        await store.SaveAsync(NewConversation("c1", "first"));

        durable.FailReads = true;
        durable.FailWrites = true;

        // First save still tries the authority and fails, marking it unhealthy.
        Conversation second = (await store.LoadAsync("c1"))!;
        second.Add(new ChatMessage(ChatRole.User, "second"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(second).AsTask());

        // Now that it is known-down, the next turn is served by the promoted tier.
        Conversation third = (await store.LoadAsync("c1"))!;
        third.Add(new ChatMessage(ChatRole.User, "third"));
        await store.SaveAsync(third);

        Assert.Equal(2, (await store.LoadAsync("c1"))!.Version);
    }

    [Fact]
    public async Task Delete_ClearsEveryTier()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        var store = new TieredConversationStore(
            new ConversationTier("redis", fast),
            new ConversationTier("cosmos", durable));
        await store.SaveAsync(NewConversation("c1", "hi"));

        await store.DeleteAsync("c1");

        Assert.Null(await fast.PeekAsync("conversation:c1"));
        Assert.Null(await durable.PeekAsync("conversation:c1"));
        Assert.Null(await store.LoadAsync("c1"));
    }

    [Fact]
    public async Task PartialDelete_IsReportedRatherThanReportedAsSuccess()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        var store = new TieredConversationStore(
            new ConversationTier("redis", fast),
            new ConversationTier("cosmos", durable));
        await store.SaveAsync(NewConversation("c1", "hi"));

        fast.FailRemoves = true;

        await Assert.ThrowsAsync<AggregateException>(() => store.DeleteAsync("c1").AsTask());
    }

    [Fact]
    public async Task WorksAsAPlainIConversationStore_ForAnAgentTurn()
    {
        IConversationStore store = new TieredConversationStore(
            new ConversationTier("redis", new FaultyStore()),
            new ConversationTier("cosmos", new FaultyStore()));
        var agent = new Agent(new FakeChatClient("orange"), instructions: "Be brief.");

        var first = new Conversation("session-1");
        await agent.RunAsync(first, "my favorite color is orange");
        await store.SaveAsync(first);

        // Another instance picks it up.
        Conversation resumed = (await store.LoadAsync("session-1"))!;
        var clientB = new FakeChatClient("orange");
        await new Agent(clientB, instructions: "Be brief.").RunAsync(resumed, "what is it?");
        await store.SaveAsync(resumed);

        Assert.Equal(4, (await store.LoadAsync("session-1"))!.Messages.Count);
    }

    [Fact]
    public void RejectsEmptyOrDuplicateTiers()
    {
        Assert.Throws<ArgumentException>(() => new TieredConversationStore([]));
        Assert.Throws<ArgumentException>(() => new TieredConversationStore(
            new ConversationTier("same", new FaultyStore()),
            new ConversationTier("same", new FaultyStore())));
    }
}
