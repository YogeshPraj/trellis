using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using NSubstitute;
using Trellis.Azure.Cosmos;

namespace Trellis.Tests;

/// <summary>
/// Contract tests for the append-only Cosmos conversation schema: that a turn writes nothing
/// but inserts, that the commit document's unique id is what detects a concurrent writer, and
/// that history is never rewritten. Cosmos's storage behaviour belongs to the SDK, so the
/// container is substituted.
/// </summary>
public class CosmosConversationStoreTests
{
    /// <summary>Captures what a save puts in its transactional batch.</summary>
    private sealed class BatchRecorder
    {
        public List<object> Created { get; } = [];

        public int ReplaceCalls { get; private set; }

        public int PatchCalls { get; private set; }

        public TransactionalBatch Batch { get; }

        public BatchRecorder(HttpStatusCode status)
        {
            Batch = Substitute.For<TransactionalBatch>();
            Batch.CreateItem(Arg.Any<object>(), Arg.Any<TransactionalBatchItemRequestOptions>())
                .Returns(call => { Created.Add(call.ArgAt<object>(0)); return Batch; });
            Batch.ReplaceItem(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<TransactionalBatchItemRequestOptions>())
                .Returns(_ => { ReplaceCalls++; return Batch; });
            Batch.PatchItem(Arg.Any<string>(), Arg.Any<IReadOnlyList<PatchOperation>>(), Arg.Any<TransactionalBatchPatchItemRequestOptions>())
                .Returns(_ => { PatchCalls++; return Batch; });

            TransactionalBatchResponse response = Substitute.For<TransactionalBatchResponse>();
            response.IsSuccessStatusCode.Returns(status == HttpStatusCode.OK);
            response.StatusCode.Returns(status);
            Batch.ExecuteAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(response));
        }
    }

    /// <summary>Feeds a single page of results to one typed query iterator.</summary>
    private static FeedIterator<T> Page<T>(IReadOnlyList<T> items)
    {
        FeedIterator<T> iterator = Substitute.For<FeedIterator<T>>();
        bool served = false;
        iterator.HasMoreResults.Returns(_ => !served);
        FeedResponse<T> response = Substitute.For<FeedResponse<T>>();
        response.GetEnumerator().Returns(_ => items.GetEnumerator());
        iterator.ReadNextAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { served = true; return Task.FromResult(response); });
        return iterator;
    }

    private static (Container Container, BatchRecorder Recorder) NewContainer(
        CosmosConversationCommit? latestCommit,
        IReadOnlyList<CosmosMessageProjection>? messages = null,
        HttpStatusCode batchStatus = HttpStatusCode.OK)
    {
        Container container = Substitute.For<Container>();
        container.GetItemQueryIterator<CosmosConversationCommit>(
                Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .ReturnsForAnyArgs(_ => Page<CosmosConversationCommit>(
                latestCommit is null ? [] : [latestCommit]));
        container.GetItemQueryIterator<CosmosMessageProjection>(
                Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .ReturnsForAnyArgs(_ => Page(messages ?? []));

        var recorder = new BatchRecorder(batchStatus);
        container.CreateTransactionalBatch(Arg.Any<PartitionKey>()).ReturnsForAnyArgs(recorder.Batch);
        return (container, recorder);
    }

    private static Conversation ConversationWith(string id, int persistedVersion, params string[] texts)
    {
        var conversation = new Conversation(id);
        foreach (string text in texts)
        {
            conversation.Add(new ChatMessage(ChatRole.User, text));
        }
        conversation.MarkPersisted(persistedVersion);
        return conversation;
    }

    [Fact]
    public async Task ASaveWritesNothingButInserts()
    {
        (Container container, BatchRecorder recorder) = NewContainer(latestCommit: null);
        var store = new CosmosConversationStore(container);

        await store.SaveAsync(ConversationWith("c1", 0, "one", "two"));

        // The whole point of the schema: no replace, no patch, anywhere.
        Assert.Equal(0, recorder.ReplaceCalls);
        Assert.Equal(0, recorder.PatchCalls);
        Assert.NotEmpty(recorder.Created);
    }

    [Fact]
    public async Task FirstSave_InsertsEveryMessageAndOneCommit()
    {
        (Container container, BatchRecorder recorder) = NewContainer(latestCommit: null);
        var store = new CosmosConversationStore(container);

        await store.SaveAsync(ConversationWith("c1", 0, "one", "two"));

        var messages = recorder.Created.OfType<CosmosConversationMessage>().ToList();
        Assert.Equal(["m-000000000", "m-000000001"], messages.Select(m => m.Id));

        CosmosConversationCommit commit = Assert.Single(recorder.Created.OfType<CosmosConversationCommit>());
        Assert.Equal("v-000000001", commit.Id);
        Assert.Equal(1, commit.Version);
        Assert.Equal(2, commit.MessageCount);
    }

    [Fact]
    public async Task SubsequentTurn_AppendsOnlyTheNewMessages()
    {
        var latest = new CosmosConversationCommit { Version = 1, MessageCount = 2, ArchivedCount = 0 };
        (Container container, BatchRecorder recorder) = NewContainer(latest);
        var store = new CosmosConversationStore(container);

        await store.SaveAsync(ConversationWith("c1", 1, "one", "two", "three"));

        // History is untouched — only ordinal 2 is written.
        CosmosConversationMessage appended = Assert.Single(recorder.Created.OfType<CosmosConversationMessage>());
        Assert.Equal("m-000000002", appended.Id);
        Assert.Equal(2, appended.Ordinal);

        CosmosConversationCommit commit = Assert.Single(recorder.Created.OfType<CosmosConversationCommit>());
        Assert.Equal(2, commit.Version);
        Assert.Equal(3, commit.MessageCount);
    }

    [Fact]
    public async Task OrdinaryTurn_WritesNoSummaryDocument()
    {
        var latest = new CosmosConversationCommit { Version = 1, MessageCount = 1, ContextEpoch = 0 };
        (Container container, BatchRecorder recorder) = NewContainer(latest);
        var store = new CosmosConversationStore(container);

        await store.SaveAsync(ConversationWith("c1", 1, "one", "two"));

        // An unchanged summary must never be rewritten — that is the write amplification the
        // schema exists to avoid.
        Assert.Empty(recorder.Created.OfType<CosmosConversationSummary>());
    }

    [Fact]
    public async Task Compaction_WritesTheSummaryOnceForItsEpoch()
    {
        var latest = new CosmosConversationCommit { Version = 3, MessageCount = 10, ArchivedCount = 0, ContextEpoch = 0 };
        (Container container, BatchRecorder recorder) = NewContainer(latest);
        var store = new CosmosConversationStore(container);

        Conversation compacted = Conversation.FromSnapshot(new ConversationSnapshot(
            "c1", 3, [new ChatMessage(ChatRole.User, "ten")], "the summary",
            ContextEpoch: 1, ArchivedCount: 9, LastInputTokenCount: null));

        await store.SaveAsync(compacted);

        CosmosConversationSummary summary = Assert.Single(recorder.Created.OfType<CosmosConversationSummary>());
        Assert.Equal("s-000000001", summary.Id);
        Assert.Equal("the summary", summary.Summary);
    }

    [Fact]
    public async Task StaleCopy_IsRejectedBeforeAnythingIsWritten()
    {
        var latest = new CosmosConversationCommit { Version = 7, MessageCount = 3 };
        (Container container, BatchRecorder recorder) = NewContainer(latest);
        var store = new CosmosConversationStore(container);

        var ex = await Assert.ThrowsAsync<ConversationConcurrencyException>(
            () => store.SaveAsync(ConversationWith("c1", 2, "one")).AsTask());

        Assert.Equal(2, ex.ExpectedVersion);
        Assert.Equal(7, ex.ActualVersion);
        Assert.Empty(recorder.Created);
    }

    [Fact]
    public async Task LosingTheCommitId_IsTheConcurrencyCheck()
    {
        // Another writer created v-000000002 first, so our insert conflicts.
        var latest = new CosmosConversationCommit { Version = 1, MessageCount = 1 };
        (Container container, _) = NewContainer(latest, batchStatus: HttpStatusCode.Conflict);
        var store = new CosmosConversationStore(container);

        await Assert.ThrowsAsync<ConversationConcurrencyException>(
            () => store.SaveAsync(ConversationWith("c1", 1, "one", "two")).AsTask());
    }

    [Fact]
    public async Task SaveAdvancesTheConversationVersion()
    {
        var latest = new CosmosConversationCommit { Version = 4, MessageCount = 1 };
        (Container container, _) = NewContainer(latest);
        var store = new CosmosConversationStore(container);

        Conversation conversation = ConversationWith("c1", 4, "one", "two");
        await store.SaveAsync(conversation);

        Assert.Equal(5, conversation.Version);
    }

    [Fact]
    public async Task UnknownConversation_LoadsAsNull()
    {
        (Container container, _) = NewContainer(latestCommit: null);

        Assert.Null(await new CosmosConversationStore(container).LoadAsync("missing"));
    }

    [Fact]
    public async Task Load_ReadsTheCommittedWindowOfMessages()
    {
        var latest = new CosmosConversationCommit { Version = 2, MessageCount = 2, ArchivedCount = 0 };
        string one = System.Text.Json.JsonSerializer.Serialize(
            new ChatMessage(ChatRole.User, "one"), AIJsonUtilities.DefaultOptions);
        string two = System.Text.Json.JsonSerializer.Serialize(
            new ChatMessage(ChatRole.Assistant, "two"), AIJsonUtilities.DefaultOptions);
        (Container container, _) = NewContainer(latest,
            messages: [new CosmosMessageProjection { Message = one }, new CosmosMessageProjection { Message = two }]);

        Conversation loaded = (await new CosmosConversationStore(container).LoadAsync("c1"))!;

        Assert.Equal(2, loaded.Version);
        Assert.Equal(["one", "two"], loaded.Messages.Select(m => m.Text));
        Assert.Equal(ChatRole.Assistant, loaded.Messages[1].Role);
    }

    [Fact]
    public async Task CompactedConversation_AppendsFromTheHotTailOnly()
    {
        var latest = new CosmosConversationCommit
        {
            Version = 3, MessageCount = 10, ArchivedCount = 8, ContextEpoch = 1,
        };
        (Container container, BatchRecorder recorder) = NewContainer(latest);
        var store = new CosmosConversationStore(container);

        Conversation conversation = Conversation.FromSnapshot(new ConversationSnapshot(
            "c1", 3, [new ChatMessage(ChatRole.User, "nine"), new ChatMessage(ChatRole.User, "ten")],
            "earlier", ContextEpoch: 1, ArchivedCount: 8, LastInputTokenCount: null));
        conversation.Add(new ChatMessage(ChatRole.User, "eleven"));

        await store.SaveAsync(conversation);

        CosmosConversationMessage appended = Assert.Single(recorder.Created.OfType<CosmosConversationMessage>());
        Assert.Equal(10, appended.Ordinal);
    }

    [Fact]
    public async Task CanServeAsATierOfATieredStore()
    {
        // The point of IReplicatedConversationStore: Cosmos composes into the chain.
        var latest = new CosmosConversationCommit { Version = 0, MessageCount = 0 };
        (Container container, BatchRecorder recorder) = NewContainer(latest);
        var fast = new InMemoryConversationStore();

        var tiered = new TieredConversationStore(
            new ConversationTier("memory", fast),
            new ConversationTier("cosmos", new CosmosConversationStore(container)));

        Assert.Equal("cosmos", tiered.AuthorityName);

        await tiered.SaveAsync(ConversationWith("c1", 0, "hello"));

        // The authority (Cosmos) committed, and the fast tier was replicated into.
        Assert.Single(recorder.Created.OfType<CosmosConversationCommit>());
        Assert.NotNull(await fast.LoadAsync("c1"));
    }

    [Fact]
    public async Task ReplicationIsUnconditional_AndStillAppendOnly()
    {
        var latest = new CosmosConversationCommit { Version = 1, MessageCount = 1 };
        (Container container, BatchRecorder recorder) = NewContainer(latest);
        var store = new CosmosConversationStore(container);

        // A replica write carries a version the authority already decided — no version check,
        // and still nothing but inserts.
        await store.ReplaceAsync(new ConversationSnapshot(
            "c1", 5, [new ChatMessage(ChatRole.User, "one"), new ChatMessage(ChatRole.User, "two")],
            null, ContextEpoch: 0, ArchivedCount: 0, LastInputTokenCount: null));

        Assert.Equal(0, recorder.ReplaceCalls);
        Assert.Equal(0, recorder.PatchCalls);
        CosmosConversationCommit commit = Assert.Single(recorder.Created.OfType<CosmosConversationCommit>());
        Assert.Equal(5, commit.Version);
        Assert.Equal("v-000000005", commit.Id);
    }

    [Fact]
    public async Task ReplicationSkipsATierAlreadyHoldingSomethingNewer()
    {
        var latest = new CosmosConversationCommit { Version = 9, MessageCount = 4 };
        (Container container, BatchRecorder recorder) = NewContainer(latest);
        var store = new CosmosConversationStore(container);

        await store.ReplaceAsync(new ConversationSnapshot(
            "c1", 5, [new ChatMessage(ChatRole.User, "old")], null, 0, 0, null));

        Assert.Empty(recorder.Created);
    }

    [Fact]
    public async Task GetVersionAsync_ReadsOnlyTheCommit()
    {
        var latest = new CosmosConversationCommit { Version = 12, MessageCount = 40 };
        (Container container, _) = NewContainer(latest);

        Assert.Equal(12, await new CosmosConversationStore(container).GetVersionAsync("c1"));
    }

    [Fact]
    public void TtlWithoutContainerSupport_FailsLoudly()
    {
        Container container = Substitute.For<Container>();

        var ex = Assert.Throws<ArgumentException>(() =>
            new CosmosConversationStore(container, TimeSpan.FromHours(1), timeToLiveEnabled: false));
        Assert.Contains("DefaultTimeToLive", ex.Message);
    }
}
