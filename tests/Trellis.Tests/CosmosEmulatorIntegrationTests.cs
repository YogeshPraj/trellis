using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Trellis.Azure.Cosmos;
using Trellis.State;

namespace Trellis.Tests;

/// <summary>
/// Runs the Cosmos providers against a real emulator. Every test no-ops when one is not
/// reachable, so the suite stays green without it.
/// </summary>
/// <remarks>
/// These cover what a substituted <c>Container</c> cannot: that a transactional batch really
/// is atomic, that a duplicate id really returns 409 — which is the entire concurrency
/// mechanism of the append-only schema — that <c>ORDER BY c.version DESC</c> behaves inside a
/// partition, and that ETag preconditions and server-side patch increments work as assumed.
/// <para>
/// Start the emulator elevated first:
/// <c>&amp; "C:\Program Files\Azure Cosmos DB Emulator\Microsoft.Azure.Cosmos.Emulator.exe" /NoUI /NoExplorer</c>
/// </para>
/// </remarks>
public class CosmosEmulatorIntegrationTests
{
    private static Conversation NewConversation(string id, params string[] texts)
    {
        var conversation = new Conversation(id);
        foreach (string text in texts)
        {
            conversation.Add(new ChatMessage(ChatRole.User, text));
        }
        return conversation;
    }

    /// <summary>Counts documents in one conversation's partition, to prove nothing was rewritten.</summary>
    private static async Task<int> CountDocumentsAsync(Container container, string conversationId)
    {
        var query = new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.cid = @cid")
            .WithParameter("@cid", conversationId);
        using FeedIterator<int> iterator = container.GetItemQueryIterator<int>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(conversationId) });
        FeedResponse<int> page = await iterator.ReadNextAsync();
        return page.FirstOrDefault();
    }

    // ---------------- conversation store ----------------

    [Fact]
    public async Task RoundTripsAConversationIncludingToolContent()
    {
        if (!await CosmosEmulatorFixture.IsAvailableAsync())
        {
            return;
        }
        await using CosmosContainerScope scope = await CosmosEmulatorFixture.CreateContainerAsync("/cid");
        var store = new CosmosConversationStore(scope.Container);

        var conversation = new Conversation("c1");
        conversation.Add(new ChatMessage(ChatRole.User, "what is the secret?"));
        conversation.Add(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "get_secret", new Dictionary<string, object?> { ["x"] = 1 })]));
        conversation.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "73")]));
        conversation.Add(new ChatMessage(ChatRole.Assistant, "it is 73"));

        await store.SaveAsync(conversation);
        Conversation loaded = (await store.LoadAsync("c1"))!;

        Assert.Equal(1, loaded.Version);
        Assert.Equal(4, loaded.Messages.Count);
        Assert.Equal(ChatRole.Tool, loaded.Messages[2].Role);
        var call = Assert.IsType<FunctionCallContent>(loaded.Messages[1].Contents[0]);
        Assert.Equal("get_secret", call.Name);
        var result = Assert.IsType<FunctionResultContent>(loaded.Messages[2].Contents[0]);
        Assert.Equal("call-1", result.CallId);
    }

    [Fact]
    public async Task ASecondTurnOnlyAddsDocuments()
    {
        if (!await CosmosEmulatorFixture.IsAvailableAsync())
        {
            return;
        }
        await using CosmosContainerScope scope = await CosmosEmulatorFixture.CreateContainerAsync("/cid");
        var store = new CosmosConversationStore(scope.Container);

        Conversation conversation = NewConversation("c1", "one", "two");
        await store.SaveAsync(conversation);
        int afterFirst = await CountDocumentsAsync(scope.Container, "c1");

        conversation.Add(new ChatMessage(ChatRole.User, "three"));
        await store.SaveAsync(conversation);
        int afterSecond = await CountDocumentsAsync(scope.Container, "c1");

        // First turn: 2 messages + 1 commit. Second: 1 message + 1 commit, nothing rewritten.
        Assert.Equal(3, afterFirst);
        Assert.Equal(5, afterSecond);
    }

    [Fact]
    public async Task ADuplicateCommitIdIsRefused_WhichIsTheConcurrencyCheck()
    {
        if (!await CosmosEmulatorFixture.IsAvailableAsync())
        {
            return;
        }
        await using CosmosContainerScope scope = await CosmosEmulatorFixture.CreateContainerAsync("/cid");
        var store = new CosmosConversationStore(scope.Container);

        Conversation first = NewConversation("c1", "one");
        await store.SaveAsync(first);

        // A second writer that loaded the same version tries to commit v2 as well.
        Conversation stale = (await store.LoadAsync("c1"))!;
        stale.Add(new ChatMessage(ChatRole.User, "from A"));
        first.Add(new ChatMessage(ChatRole.User, "from B"));

        await store.SaveAsync(first);

        await Assert.ThrowsAsync<ConversationConcurrencyException>(() => store.SaveAsync(stale).AsTask());
        Assert.Equal("from B", (await store.LoadAsync("c1"))!.Messages[^1].Text);
    }

    [Fact]
    public async Task ConcurrentSaversProduceExactlyOneWinner()
    {
        if (!await CosmosEmulatorFixture.IsAvailableAsync())
        {
            return;
        }
        await using CosmosContainerScope scope = await CosmosEmulatorFixture.CreateContainerAsync("/cid");
        var store = new CosmosConversationStore(scope.Container);
        await store.SaveAsync(NewConversation("c1", "seed"));

        // Eight writers all holding version 1, racing to commit version 2.
        Conversation[] contenders = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(async i =>
            {
                Conversation c = (await store.LoadAsync("c1"))!;
                c.Add(new ChatMessage(ChatRole.User, "writer-" + i));
                return c;
            }));

        int committed = 0;
        int rejected = 0;
        await Task.WhenAll(contenders.Select(async c =>
        {
            try
            {
                await store.SaveAsync(c);
                Interlocked.Increment(ref committed);
            }
            catch (ConversationConcurrencyException)
            {
                Interlocked.Increment(ref rejected);
            }
        }));

        Assert.Equal(1, committed);
        Assert.Equal(7, rejected);
        Assert.Equal(2, (await store.LoadAsync("c1"))!.Version);
    }

    [Fact]
    public async Task TheNewestCommitIsTheOneRead()
    {
        if (!await CosmosEmulatorFixture.IsAvailableAsync())
        {
            return;
        }
        await using CosmosContainerScope scope = await CosmosEmulatorFixture.CreateContainerAsync("/cid");
        var store = new CosmosConversationStore(scope.Container);

        Conversation conversation = NewConversation("c1", "one");
        for (int turn = 0; turn < 12; turn++)
        {
            await store.SaveAsync(conversation);
            conversation.Add(new ChatMessage(ChatRole.User, "turn-" + turn));
        }

        // Twelve commit documents exist; ORDER BY version DESC must find the newest, and the
        // zero-padded ids must not sort v-10 before v-9.
        Assert.Equal(12, (await store.LoadAsync("c1"))!.Version);
        Assert.Equal(12, await store.GetVersionAsync("c1"));
    }

    [Fact]
    public async Task CompactionWritesTheSummaryOnceAndLoadReconstructsIt()
    {
        if (!await CosmosEmulatorFixture.IsAvailableAsync())
        {
            return;
        }
        await using CosmosContainerScope scope = await CosmosEmulatorFixture.CreateContainerAsync("/cid");
        var store = new CosmosConversationStore(scope.Container);

        Conversation conversation = NewConversation("c1", "one", "two", "three");
        await store.SaveAsync(conversation);

        Conversation compacted = Conversation.FromSnapshot(new ConversationSnapshot(
            "c1", conversation.Version, [new ChatMessage(ChatRole.User, "three")],
            "the earlier turns", ContextEpoch: 1, ArchivedCount: 2, LastInputTokenCount: 4321));
        await store.SaveAsync(compacted);

        Conversation loaded = (await store.LoadAsync("c1"))!;
        Assert.Equal("the earlier turns", loaded.Summary);
        Assert.Equal(1, loaded.ContextEpoch);
        Assert.Equal(2, loaded.ArchivedCount);
        Assert.Equal(4321, loaded.LastInputTokenCount);
        Assert.Equal("three", Assert.Single(loaded.Messages).Text);
    }

    [Fact]
    public async Task DeleteRemovesEveryDocumentInThePartition()
    {
        if (!await CosmosEmulatorFixture.IsAvailableAsync())
        {
            return;
        }
        await using CosmosContainerScope scope = await CosmosEmulatorFixture.CreateContainerAsync("/cid");
        var store = new CosmosConversationStore(scope.Container);
        await store.SaveAsync(NewConversation("c1", "one", "two"));

        await store.DeleteAsync("c1");

        Assert.Null(await store.LoadAsync("c1"));
        Assert.Equal(0, await CountDocumentsAsync(scope.Container, "c1"));
    }

    [Fact]
    public async Task ReplicationIsUnconditionalAndIdempotent()
    {
        if (!await CosmosEmulatorFixture.IsAvailableAsync())
        {
            return;
        }
        await using CosmosContainerScope scope = await CosmosEmulatorFixture.CreateContainerAsync("/cid");
        var store = new CosmosConversationStore(scope.Container);

        var snapshot = new ConversationSnapshot(
            "c1", 7, [new ChatMessage(ChatRole.User, "replicated")], null, 0, 0, null);

        await store.ReplaceAsync(snapshot);
        await store.ReplaceAsync(snapshot);   // a duplicate replica write is success, not a conflict

        Conversation loaded = (await store.LoadAsync("c1"))!;
        Assert.Equal(7, loaded.Version);
        Assert.Equal("replicated", Assert.Single(loaded.Messages).Text);
    }

    [Fact]
    public async Task UnknownConversationLoadsAsNull()
    {
        if (!await CosmosEmulatorFixture.IsAvailableAsync())
        {
            return;
        }
        await using CosmosContainerScope scope = await CosmosEmulatorFixture.CreateContainerAsync("/cid");

        Assert.Null(await new CosmosConversationStore(scope.Container).LoadAsync("never-saved"));
    }

    [Fact]
    public async Task CosmosServesAsTheAuthoritativeTier()
    {
        if (!await CosmosEmulatorFixture.IsAvailableAsync())
        {
            return;
        }
        await using CosmosContainerScope scope = await CosmosEmulatorFixture.CreateContainerAsync("/cid");

        var tiered = new TieredConversationStore(
            new ConversationTier("memory", new InMemoryConversationStore()),
            new ConversationTier("cosmos", new CosmosConversationStore(scope.Container)));

        Assert.Equal("cosmos", tiered.AuthorityName);

        Conversation conversation = NewConversation("c1", "hello");
        await tiered.SaveAsync(conversation);
        conversation.Add(new ChatMessage(ChatRole.User, "again"));
        await tiered.SaveAsync(conversation);

        Conversation loaded = (await tiered.LoadAsync("c1"))!;
        Assert.Equal(2, loaded.Version);
        Assert.Equal(2, loaded.Messages.Count);
    }

    // ---------------- shared state store ----------------

    [Fact]
    public async Task SharedState_RoundTripsAndRemoves()
    {
        if (!await CosmosEmulatorFixture.IsAvailableAsync())
        {
            return;
        }
        await using CosmosContainerScope scope = await CosmosEmulatorFixture.CreateContainerAsync("/pk");
        var store = new CosmosSharedStateStore(scope.Container);

        Assert.Null(await store.GetAsync("k"));
        await store.SetAsync("k", "v");
        Assert.Equal("v", await store.GetAsync("k"));

        await store.RemoveAsync("k");
        Assert.Null(await store.GetAsync("k"));
    }

    [Fact]
    public async Task SharedState_CompareAndSwapHonoursTheETag()
    {
        if (!await CosmosEmulatorFixture.IsAvailableAsync())
        {
            return;
        }
        await using CosmosContainerScope scope = await CosmosEmulatorFixture.CreateContainerAsync("/pk");
        IAtomicSharedStateStore store = new CosmosSharedStateStore(scope.Container);

        Assert.True(await store.TrySetIfUnchangedAsync("k", expectedValue: null, "first"));
        Assert.False(await store.TrySetIfUnchangedAsync("k", expectedValue: null, "again"));
        Assert.False(await store.TrySetIfUnchangedAsync("k", "wrong", "second"));
        Assert.True(await store.TrySetIfUnchangedAsync("k", "first", "second"));

        Assert.Equal("second", await store.GetAsync("k"));
    }

    [Fact]
    public async Task SharedState_ConcurrentSwapsElectOneWinner()
    {
        if (!await CosmosEmulatorFixture.IsAvailableAsync())
        {
            return;
        }
        await using CosmosContainerScope scope = await CosmosEmulatorFixture.CreateContainerAsync("/pk");
        IAtomicSharedStateStore store = new CosmosSharedStateStore(scope.Container);
        await store.SetAsync("k", "start");

        bool[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => store.TrySetIfUnchangedAsync("k", "start", "w" + i).AsTask()));

        // This is what makes the tiered store's optimistic concurrency real rather than assumed.
        Assert.Equal(1, results.Count(won => won));
    }

    [Fact]
    public async Task SharedState_ConcurrentIncrementsLoseNothing()
    {
        if (!await CosmosEmulatorFixture.IsAvailableAsync())
        {
            return;
        }
        await using CosmosContainerScope scope = await CosmosEmulatorFixture.CreateContainerAsync("/pk");
        ISharedStateStore store = new CosmosSharedStateStore(scope.Container);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.IncrementAsync("counter").AsTask()));

        Assert.Equal("20", (await store.IncrementAsync("counter")).ToString());
    }

    [Fact]
    public async Task SharedState_AppendsKeepChronologicalOrder()
    {
        if (!await CosmosEmulatorFixture.IsAvailableAsync())
        {
            return;
        }
        await using CosmosContainerScope scope = await CosmosEmulatorFixture.CreateContainerAsync("/pk");
        ISharedStateStore store = new CosmosSharedStateStore(scope.Container);

        for (int i = 0; i < 5; i++)
        {
            await store.AppendAsync("list", "entry-" + i);
        }

        Assert.Equal(["entry-0", "entry-1", "entry-2", "entry-3", "entry-4"], await store.GetListAsync("list"));
    }

    [Fact]
    public async Task SharedState_TtlIsAcceptedByTheContainer()
    {
        if (!await CosmosEmulatorFixture.IsAvailableAsync())
        {
            return;
        }
        await using CosmosContainerScope scope = await CosmosEmulatorFixture.CreateContainerAsync("/pk");
        var store = new CosmosSharedStateStore(scope.Container);

        // Proves the ttl property is written in a shape Cosmos accepts, not that it expires.
        await store.SetAsync("k", "v", TimeSpan.FromHours(1));

        Assert.Equal("v", await store.GetAsync("k"));
    }
}
