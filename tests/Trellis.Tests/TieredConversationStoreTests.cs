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

    /// <summary>Wraps a fault-injecting KV fake as the conversation store a tier now requires.</summary>
    private static ConversationTier Tier(string name, ISharedStateStore store, TimeSpan? timeToLive = null) =>
        new(name, new SharedStateConversationStore(store, timeToLive), timeToLive);

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
            Tier("redis", fast),
            Tier("cosmos", durable));

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
            Tier("memory", t1),
            Tier("redis", t2),
            Tier("cosmos", t3),
            Tier("blob", t4));

        await store.SaveAsync(NewConversation("c1", "hello"));

        Assert.Equal("blob", store.AuthorityName);
        foreach (FaultyStore tier in new[] { t1, t2, t3, t4 })
        {
            Assert.NotNull(await tier.PeekAsync("conversation:c1"));
        }
        Assert.NotNull(await store.LoadAsync("c1"));
    }

    /// <summary>A store whose writes take a fixed time, for observing replication concurrency.</summary>
    private sealed class SlowStore(TimeSpan writeDelay) : IAtomicSharedStateStore
    {
        private readonly InMemorySharedStateStore _inner = new();

        public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.GetAsync(key, cancellationToken);

        public async ValueTask SetAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(writeDelay, cancellationToken);
            await _inner.SetAsync(key, value, timeToLive, cancellationToken);
        }

        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.RemoveAsync(key, cancellationToken);

        public ValueTask<bool> TrySetIfUnchangedAsync(
            string key, string? expectedValue, string newValue, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default) =>
            _inner.TrySetIfUnchangedAsync(key, expectedValue, newValue, timeToLive, cancellationToken);

        public ValueTask<long> IncrementAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.IncrementAsync(key, cancellationToken);

        public ValueTask<long> AppendAsync(string key, string value, CancellationToken cancellationToken = default) =>
            _inner.AppendAsync(key, value, cancellationToken);

        public ValueTask<IReadOnlyList<string>> GetListAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.GetListAsync(key, cancellationToken);
    }

    [Fact]
    public async Task ReplicasAreWrittenConcurrently_NotOneAfterAnother()
    {
        var delay = TimeSpan.FromMilliseconds(200);
        var store = new TieredConversationStore(
            Tier("r1", new SlowStore(delay)),
            Tier("r2", new SlowStore(delay)),
            Tier("r3", new SlowStore(delay)),
            Tier("authority", new FaultyStore()));

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        await store.SaveAsync(NewConversation("c1", "hello"));
        TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start);

        // Three 200ms replicas: concurrent is ~200ms, sequential would be ~600ms.
        Assert.True(elapsed < TimeSpan.FromMilliseconds(500), $"replicas were written serially ({elapsed})");
    }

    [Fact]
    public async Task ReadsPreferTheFastestTier()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        var store = new TieredConversationStore(
            Tier("redis", fast),
            Tier("cosmos", durable));
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
            Tier("redis", fast),
            Tier("cosmos", durable));
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
            Tier("redis", new FaultyStore()),
            Tier("cosmos", new FaultyStore()));
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
            [Tier("redis", fast), Tier("cosmos", durable)],
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
            [Tier("redis", fast), Tier("cosmos", durable)],
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
            [Tier("redis", fast), Tier("cosmos", durable)],
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
                Tier("redis", fast, TimeSpan.FromMinutes(10)),
                Tier("cosmos", durable),
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
            Tier("redis", fast),
            Tier("cosmos", durable));

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
            [Tier("redis", fast), Tier("cosmos", durable)],
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
            Tier("redis", fast),
            Tier("cosmos", durable));
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
            Tier("redis", fast),
            Tier("cosmos", durable));
        await store.SaveAsync(NewConversation("c1", "hi"));

        fast.FailRemoves = true;

        await Assert.ThrowsAsync<AggregateException>(() => store.DeleteAsync("c1").AsTask());
    }

    [Fact]
    public async Task WorksAsAPlainIConversationStore_ForAnAgentTurn()
    {
        IConversationStore store = new TieredConversationStore(
            Tier("redis", new FaultyStore()),
            Tier("cosmos", new FaultyStore()));
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

    private static TieredConversationStore WriteBehind(
        FaultyStore fast, FaultyStore durable, TieredConversationStoreOptions? options = null) =>
        new([Tier("redis", fast), Tier("cosmos", durable)],
            options ?? new TieredConversationStoreOptions
            {
                ReplicationMode = ReplicationMode.WriteBehind,
                // Long interval so the background flusher never races the test: every case
                // drives replication explicitly through FlushAsync or DisposeAsync.
                FlushInterval = TimeSpan.FromMinutes(10),
                UnhealthyCooldown = TimeSpan.FromMilliseconds(50),
                MaxUnhealthyCooldown = TimeSpan.FromMilliseconds(50),
            });

    [Fact]
    public async Task WriteBehind_ReturnsOnceTheFastTierHasIt()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        await using TieredConversationStore store = WriteBehind(fast, durable);

        await store.SaveAsync(NewConversation("c1", "hello"));

        // The fast tier is the authority now, and it has the turn immediately.
        Assert.Equal("redis", store.AuthorityName);
        Assert.NotNull(await fast.PeekAsync("conversation:c1"));
        // The durable tier is updated by the flusher, so it is not required to be there yet.
        Assert.Equal(1, store.PendingReplicationCount);
    }

    [Fact]
    public async Task WriteBehind_FlushReachesTheDurableTier()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        await using TieredConversationStore store = WriteBehind(fast, durable);

        await store.SaveAsync(NewConversation("c1", "hello"));
        await store.FlushAsync();

        Assert.NotNull(await durable.PeekAsync("conversation:c1"));
        Assert.Equal(0, store.PendingReplicationCount);
    }

    [Fact]
    public async Task WriteBehind_CoalescesManyTurnsIntoOneReplication()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        await using TieredConversationStore store = WriteBehind(fast, durable);

        var conversation = NewConversation("c1", "turn one");
        for (int i = 0; i < 20; i++)
        {
            conversation.Add(new ChatMessage(ChatRole.User, "turn " + i));
            await store.SaveAsync(conversation);
        }

        // Twenty turns, one pending entry — the map is bounded by conversations, not traffic.
        Assert.Equal(1, store.PendingReplicationCount);

        int writesBefore = durable.Writes;
        await store.FlushAsync();
        Assert.Equal(writesBefore + 1, durable.Writes);
        Assert.Equal(20, (await store.LoadAsync("c1"))!.Version);
    }

    [Fact]
    public async Task WriteBehind_DurableTierDown_DoesNotFailTheTurn()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore { FailWrites = true, FailReads = true };
        List<string> failures = [];
        await using TieredConversationStore store = WriteBehind(fast, durable, new TieredConversationStoreOptions
        {
            ReplicationMode = ReplicationMode.WriteBehind,
            FlushInterval = TimeSpan.FromMilliseconds(20),
            OnReplicationFailed = (id, _) => failures.Add(id),
        });

        await store.SaveAsync(NewConversation("c1", "hello"));   // must not throw
        await store.FlushAsync();

        Assert.Equal("hello", (await store.LoadAsync("c1"))!.Messages[0].Text);
    }

    [Fact]
    public async Task WriteBehind_RetriesUntilTheDurableTierActuallyTakesIt()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore { FailWrites = true };
        await using TieredConversationStore store = WriteBehind(fast, durable);

        await store.SaveAsync(NewConversation("c1", "hello"));
        await store.FlushAsync();

        // The write must NOT be discarded: without a retry the turn would be missing from the
        // durable tier for good once this conversation went idle.
        Assert.Equal(1, store.PendingReplicationCount);
        Assert.Null(await durable.PeekAsync("conversation:c1"));

        durable.FailWrites = false;
        // The tier is cooling down after its failure; once that expires the retry lands.
        for (int attempt = 0; attempt < 60 && store.PendingReplicationCount > 0; attempt++)
        {
            await Task.Delay(60);
            await store.FlushAsync();
        }

        Assert.Equal(0, store.PendingReplicationCount);
        Assert.NotNull(await durable.PeekAsync("conversation:c1"));
    }

    [Fact]
    public async Task WriteBehind_DisposalFlushesSoAGracefulShutdownLosesNothing()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        var store = WriteBehind(fast, durable);

        await store.SaveAsync(NewConversation("c1", "hello"));
        await store.DisposeAsync();

        Assert.NotNull(await durable.PeekAsync("conversation:c1"));
    }

    [Fact]
    public async Task WriteBehind_StaleReplicaIsTimeTravelled_NotCorrupt()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        await using TieredConversationStore store = WriteBehind(fast, durable);

        var conversation = NewConversation("c1", "turn one");
        await store.SaveAsync(conversation);
        await store.FlushAsync();                      // durable holds v1

        conversation.Add(new ChatMessage(ChatRole.User, "turn two"));
        await store.SaveAsync(conversation);           // v2 pending only

        // Snapshots are cumulative, so the durable tier holds a complete older conversation
        // rather than a partial one — losing the pending write costs turns, never coherence.
        Conversation durableCopy = (await new SharedStateConversationStore(durable).LoadAsync("c1"))!;
        Assert.Equal(1, durableCopy.Version);
        Assert.Equal("turn one", Assert.Single(durableCopy.Messages).Text);
    }

    [Fact]
    public async Task WriteBehind_OlderFlushCannotOverwriteANewerSnapshot()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        await using TieredConversationStore store = WriteBehind(fast, durable);

        var conversation = NewConversation("c1", "turn one");
        await store.SaveAsync(conversation);
        conversation.Add(new ChatMessage(ChatRole.User, "turn two"));
        await store.SaveAsync(conversation);
        await store.FlushAsync();                      // durable now at v2

        // A late flush carrying v1 (another instance, delayed) must not regress the replica.
        await store.FlushAsync();
        Conversation durableCopy = (await new SharedStateConversationStore(durable).LoadAsync("c1"))!;
        Assert.Equal(2, durableCopy.Version);
    }

    [Fact]
    public async Task WriteBehind_DeleteIsNotUndoneByAPendingReplication()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        await using TieredConversationStore store = WriteBehind(fast, durable);

        await store.SaveAsync(NewConversation("c1", "hello"));   // queued, not yet replicated
        await store.DeleteAsync("c1");
        await store.FlushAsync();

        // The flusher must not resurrect what the caller deleted.
        Assert.Null(await durable.PeekAsync("conversation:c1"));
        Assert.Null(await store.LoadAsync("c1"));
    }

    [Fact]
    public async Task WriteBehind_SaveAfterDisposalIsRefused_NotSilentlyDropped()
    {
        var fast = new FaultyStore();
        var durable = new FaultyStore();
        var store = WriteBehind(fast, durable);
        await store.DisposeAsync();

        // Queuing into a map nothing will drain would tell the caller it saved when it did not.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => store.SaveAsync(NewConversation("c1", "hello")).AsTask());
    }

    [Fact]
    public void RejectsEmptyOrDuplicateTiers()
    {
        Assert.Throws<ArgumentException>(() => new TieredConversationStore([]));
        Assert.Throws<ArgumentException>(() => new TieredConversationStore(
            Tier("same", new FaultyStore()),
            Tier("same", new FaultyStore())));
    }
}
