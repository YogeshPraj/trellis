using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Trellis.Azure.Cosmos;

namespace Trellis.Tests;

/// <summary>
/// Contract tests for the append-only Cosmos conversation schema: that a turn appends new
/// messages rather than rewriting history, that the head is patched (not replaced) under an
/// ETag precondition, and that both commit in one transactional batch. Cosmos's own storage
/// behaviour belongs to the SDK, so the container is substituted.
/// </summary>
public class CosmosConversationStoreTests
{
    private sealed class BatchRecorder
    {
        public List<object> Created { get; } = [];

        public List<(string Id, IReadOnlyList<PatchOperation> Ops, string? ETag)> Patched { get; } = [];

        public TransactionalBatch Batch { get; }

        public BatchRecorder(HttpStatusCode status = HttpStatusCode.OK)
        {
            Batch = Substitute.For<TransactionalBatch>();
            Batch.CreateItem(Arg.Any<object>(), Arg.Any<TransactionalBatchItemRequestOptions>())
                .Returns(call => { Created.Add(call.ArgAt<object>(0)); return Batch; });
            Batch.PatchItem(Arg.Any<string>(), Arg.Any<IReadOnlyList<PatchOperation>>(), Arg.Any<TransactionalBatchPatchItemRequestOptions>())
                .Returns(call =>
                {
                    Patched.Add((call.ArgAt<string>(0),
                                 call.ArgAt<IReadOnlyList<PatchOperation>>(1),
                                 call.ArgAt<TransactionalBatchPatchItemRequestOptions>(2)?.IfMatchEtag));
                    return Batch;
                });

            TransactionalBatchResponse response = Substitute.For<TransactionalBatchResponse>();
            response.IsSuccessStatusCode.Returns(status == HttpStatusCode.OK);
            response.StatusCode.Returns(status);
            Batch.ExecuteAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(response));
        }
    }

    private static ItemResponse<T> Response<T>(T resource, string? etag = null)
    {
        ItemResponse<T> response = Substitute.For<ItemResponse<T>>();
        response.Resource.Returns(resource);
        response.ETag.Returns(etag);
        return response;
    }

    private static CosmosException NotFound() => new("not found", HttpStatusCode.NotFound, 0, "a", 1);

    private static (Container Container, BatchRecorder Recorder) NewContainer(
        CosmosConversationHead? head, string? etag = null, HttpStatusCode batchStatus = HttpStatusCode.OK)
    {
        Container container = Substitute.For<Container>();
        if (head is null)
        {
            container.ReadItemAsync<CosmosConversationHead>(
                    Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
                .ThrowsAsyncForAnyArgs(NotFound());
        }
        else
        {
            container.ReadItemAsync<CosmosConversationHead>(
                    Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
                .ReturnsForAnyArgs(_ => Task.FromResult(Response(head, etag)));
        }

        var recorder = new BatchRecorder(batchStatus);
        container.CreateTransactionalBatch(Arg.Any<PartitionKey>()).ReturnsForAnyArgs(recorder.Batch);
        return (container, recorder);
    }

    private static Conversation ConversationWith(string id, params string[] texts)
    {
        var conversation = new Conversation(id);
        foreach (string text in texts)
        {
            conversation.Add(new ChatMessage(ChatRole.User, text));
        }
        return conversation;
    }

    [Fact]
    public async Task FirstSave_CreatesHeadAndEveryMessage()
    {
        (Container container, BatchRecorder recorder) = NewContainer(head: null);
        var store = new CosmosConversationStore(container);

        await store.SaveAsync(ConversationWith("c1", "one", "two"));

        var messages = recorder.Created.OfType<CosmosConversationMessage>().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal(["m-000000000", "m-000000001"], messages.Select(m => m.Id));
        Assert.All(messages, m => Assert.Equal("c1", m.ConversationId));

        // No head exists yet, so it is created rather than patched.
        CosmosConversationHead head = Assert.Single(recorder.Created.OfType<CosmosConversationHead>());
        Assert.Equal(1, head.Version);
        Assert.Equal(2, head.MessageCount);
        Assert.Empty(recorder.Patched);
    }

    [Fact]
    public async Task SubsequentTurn_AppendsOnlyNewMessages_AndPatchesTheHead()
    {
        var head = new CosmosConversationHead
        {
            ConversationId = "c1", Version = 1, MessageCount = 2, ArchivedCount = 0,
        };
        (Container container, BatchRecorder recorder) = NewContainer(head, etag: "etag-1");
        var store = new CosmosConversationStore(container);

        Conversation conversation = ConversationWith("c1", "one", "two", "three");
        conversation.MarkPersisted(1);

        await store.SaveAsync(conversation);

        // The two existing messages are untouched — this is the whole point of the schema.
        CosmosConversationMessage appended = Assert.Single(recorder.Created.OfType<CosmosConversationMessage>());
        Assert.Equal("m-000000002", appended.Id);
        Assert.Equal(2, appended.Ordinal);

        // The head is patched, not replaced, and only under the ETag we read.
        (string id, IReadOnlyList<PatchOperation> ops, string? etag) = Assert.Single(recorder.Patched);
        Assert.Equal("head", id);
        Assert.Equal("etag-1", etag);
        Assert.Contains(ops, o => o.Path == "/version");
        Assert.Contains(ops, o => o.Path == "/messageCount");
        Assert.Empty(recorder.Created.OfType<CosmosConversationHead>());
    }

    [Fact]
    public async Task SaveAdvancesTheConversationVersion()
    {
        var head = new CosmosConversationHead { ConversationId = "c1", Version = 4, MessageCount = 1 };
        (Container container, _) = NewContainer(head, etag: "e");
        var store = new CosmosConversationStore(container);

        Conversation conversation = ConversationWith("c1", "one", "two");
        conversation.MarkPersisted(4);

        await store.SaveAsync(conversation);

        Assert.Equal(5, conversation.Version);
    }

    [Fact]
    public async Task StaleCopy_IsRejectedBeforeAnyWrite()
    {
        var head = new CosmosConversationHead { ConversationId = "c1", Version = 7, MessageCount = 3 };
        (Container container, BatchRecorder recorder) = NewContainer(head, etag: "e");
        var store = new CosmosConversationStore(container);

        Conversation stale = ConversationWith("c1", "one");
        stale.MarkPersisted(2);

        var ex = await Assert.ThrowsAsync<ConversationConcurrencyException>(() => store.SaveAsync(stale).AsTask());

        Assert.Equal(2, ex.ExpectedVersion);
        Assert.Equal(7, ex.ActualVersion);
        Assert.Empty(recorder.Created);
        Assert.Empty(recorder.Patched);
    }

    [Fact]
    public async Task LostETagRace_SurfacesAsAConcurrencyConflict()
    {
        var head = new CosmosConversationHead { ConversationId = "c1", Version = 1, MessageCount = 1 };
        (Container container, _) = NewContainer(head, etag: "e", batchStatus: HttpStatusCode.PreconditionFailed);
        var store = new CosmosConversationStore(container);

        Conversation conversation = ConversationWith("c1", "one", "two");
        conversation.MarkPersisted(1);

        await Assert.ThrowsAsync<ConversationConcurrencyException>(() => store.SaveAsync(conversation).AsTask());
    }

    [Fact]
    public async Task UnknownConversation_LoadsAsNull()
    {
        (Container container, _) = NewContainer(head: null);

        Assert.Null(await new CosmosConversationStore(container).LoadAsync("missing"));
    }

    [Fact]
    public async Task CompactedConversation_AppendsFromTheHotTailOnly()
    {
        // 10 messages committed, 8 compacted away: the live conversation holds ordinals 8-9
        // plus one new turn, and only the new one is appendable.
        var head = new CosmosConversationHead
        {
            ConversationId = "c1", Version = 3, MessageCount = 10, ArchivedCount = 8, Summary = "earlier",
        };
        (Container container, BatchRecorder recorder) = NewContainer(head, etag: "e");
        var store = new CosmosConversationStore(container);

        Conversation conversation = Conversation.FromSnapshot(new ConversationSnapshot(
            "c1", 3,
            [new ChatMessage(ChatRole.User, "nine"), new ChatMessage(ChatRole.User, "ten")],
            "earlier", ContextEpoch: 1, ArchivedCount: 8, LastInputTokenCount: null));
        conversation.Add(new ChatMessage(ChatRole.User, "eleven"));

        await store.SaveAsync(conversation);

        CosmosConversationMessage appended = Assert.Single(recorder.Created.OfType<CosmosConversationMessage>());
        Assert.Equal(10, appended.Ordinal);
    }

    [Fact]
    public void TtlWithoutContainerSupport_FailsLoudly()
    {
        Container container = Substitute.For<Container>();

        var ex = Assert.Throws<ArgumentException>(() =>
            new CosmosConversationStore(container, TimeSpan.FromHours(1), timeToLiveEnabled: false));
        Assert.Contains("DefaultTimeToLive", ex.Message);
    }

    [Fact]
    public async Task NoNewMessages_StillCommitsTheHeadPatch()
    {
        var head = new CosmosConversationHead { ConversationId = "c1", Version = 1, MessageCount = 1 };
        (Container container, BatchRecorder recorder) = NewContainer(head, etag: "e");
        var store = new CosmosConversationStore(container);

        // A compaction-only save changes the summary and epoch without adding messages.
        Conversation conversation = ConversationWith("c1", "one");
        conversation.MarkPersisted(1);

        await store.SaveAsync(conversation);

        Assert.Empty(recorder.Created.OfType<CosmosConversationMessage>());
        Assert.Single(recorder.Patched);
    }
}
