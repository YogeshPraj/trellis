using Microsoft.Extensions.AI;
using Trellis.State;

namespace Trellis.Tests;

public class ConversationStoreTests
{
    public static TheoryData<Func<IConversationStore>> Stores => new()
    {
        () => new InMemoryConversationStore(),
        () => new SharedStateConversationStore(new InMemorySharedStateStore()),
    };

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task RoundTrips_TheWholeConversationState(Func<IConversationStore> factory)
    {
        IConversationStore store = factory();
        var agent = new Agent(new FakeChatClient("reply one", "reply two"));
        var compactor = new ConversationCompactor(
            new StubSummarizer("the summary"),
            options: new CompactionOptions { MaxHotMessages = 2, KeepRecentMessages = 1 });
        var original = new Conversation("conv-1");

        await agent.RunAsync(original, "one");
        await agent.RunAsync(original, "two");
        await compactor.CompactIfNeededAsync(original);

        await store.SaveAsync(original);
        Conversation? loaded = await store.LoadAsync("conv-1");

        Assert.NotNull(loaded);
        Assert.Equal("conv-1", loaded.Id);
        Assert.Equal("the summary", loaded.Summary);
        Assert.Equal(original.ContextEpoch, loaded.ContextEpoch);
        Assert.Equal(original.ArchivedCount, loaded.ArchivedCount);
        Assert.Equal(original.RoutingId, loaded.RoutingId);
        Assert.Equal(
            original.Messages.Select(m => (m.Role.Value, m.Text)),
            loaded.Messages.Select(m => (m.Role.Value, m.Text)));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task UnknownConversation_LoadsAsNull(Func<IConversationStore> factory)
    {
        Assert.Null(await factory().LoadAsync("never-saved"));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ResumedConversation_ContinuesOnAnotherInstance(Func<IConversationStore> factory)
    {
        IConversationStore store = factory();
        var agent = new Agent(new FakeChatClient("orange"), instructions: "Be brief.");

        // "Instance A" handles the first turn and persists.
        var first = new Conversation("conv-2");
        await agent.RunAsync(first, "my favorite color is orange");
        await store.SaveAsync(first);

        // "Instance B" picks the conversation up cold.
        Conversation resumed = (await store.LoadAsync("conv-2"))!;
        var clientB = new FakeChatClient("orange again");
        await new Agent(clientB, instructions: "Be brief.").RunAsync(resumed, "what is it?");

        IReadOnlyList<ChatMessage> sent = Assert.Single(clientB.Requests);
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.Equal("my favorite color is orange", sent[1].Text);
        Assert.Equal("orange", sent[2].Text);
        Assert.Equal("what is it?", sent[3].Text);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ConcurrentWriter_IsRejected_NotSilentlyOverwritten(Func<IConversationStore> factory)
    {
        IConversationStore store = factory();
        var conversation = new Conversation("conv-3");
        conversation.Add(new ChatMessage(ChatRole.User, "first"));
        await store.SaveAsync(conversation);

        // Two instances load the same version...
        Conversation instanceA = (await store.LoadAsync("conv-3"))!;
        Conversation instanceB = (await store.LoadAsync("conv-3"))!;
        instanceA.Add(new ChatMessage(ChatRole.User, "from A"));
        instanceB.Add(new ChatMessage(ChatRole.User, "from B"));

        await store.SaveAsync(instanceA);
        var ex = await Assert.ThrowsAsync<ConversationConcurrencyException>(() => store.SaveAsync(instanceB).AsTask());

        Assert.Equal("conv-3", ex.ConversationId);
        Assert.Equal(1, ex.ExpectedVersion);
        Assert.Equal(2, ex.ActualVersion);

        // A's turn survived; B's was refused rather than clobbering it.
        Conversation latest = (await store.LoadAsync("conv-3"))!;
        Assert.Equal("from A", latest.Messages[^1].Text);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task VersionAdvancesOnEverySave(Func<IConversationStore> factory)
    {
        IConversationStore store = factory();
        var conversation = new Conversation("conv-4");

        Assert.Equal(0, conversation.Version);
        await store.SaveAsync(conversation);
        Assert.Equal(1, conversation.Version);
        await store.SaveAsync(conversation);
        Assert.Equal(2, conversation.Version);

        Assert.Equal(2, (await store.LoadAsync("conv-4"))!.Version);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Delete_RemovesTheConversation(Func<IConversationStore> factory)
    {
        IConversationStore store = factory();
        var conversation = new Conversation("conv-5");
        await store.SaveAsync(conversation);

        await store.DeleteAsync("conv-5");

        Assert.Null(await store.LoadAsync("conv-5"));
        await store.DeleteAsync("conv-5"); // deleting twice is not an error
    }

    [Fact]
    public async Task ToolCallHistory_SurvivesTheRoundTrip()
    {
        var store = new SharedStateConversationStore(new InMemorySharedStateStore());
        var conversation = new Conversation("conv-tools");
        conversation.Add(new ChatMessage(ChatRole.User, "what is the secret?"));
        conversation.Add(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "get_secret", new Dictionary<string, object?> { ["x"] = 1 })]));
        conversation.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "73")]));

        await store.SaveAsync(conversation);
        Conversation loaded = (await store.LoadAsync("conv-tools"))!;

        var call = Assert.IsType<FunctionCallContent>(loaded.Messages[1].Contents[0]);
        Assert.Equal("get_secret", call.Name);
        var result = Assert.IsType<FunctionResultContent>(loaded.Messages[2].Contents[0]);
        Assert.Equal("call-1", result.CallId);
    }

    [Fact]
    public async Task LastInputTokenCount_SurvivesSoBudgetsStayCorrectAfterFailover()
    {
        var store = new InMemoryConversationStore();
        var agent = new Agent(new UsageClient(4321));
        var conversation = new Conversation("conv-usage");
        await agent.RunAsync(conversation, "hi");

        await store.SaveAsync(conversation);

        Assert.Equal(4321, (await store.LoadAsync("conv-usage"))!.LastInputTokenCount);
    }

    [Fact]
    public void RequireAtomicStore_RejectsANonAtomicBackend()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SharedStateConversationStore(new NonAtomicStore(), requireAtomicStore: true));

        Assert.Contains(nameof(IAtomicSharedStateStore), ex.Message);
    }

    [Fact]
    public void IsAtomic_ReflectsTheBackend()
    {
        Assert.True(new SharedStateConversationStore(new InMemorySharedStateStore()).IsAtomic);
        Assert.False(new SharedStateConversationStore(new NonAtomicStore()).IsAtomic);
    }

    [Fact]
    public async Task NonAtomicBackend_StillCatchesTheCommonConflict()
    {
        var store = new SharedStateConversationStore(new NonAtomicStore());
        var conversation = new Conversation("conv-6");
        await store.SaveAsync(conversation);

        Conversation stale = (await store.LoadAsync("conv-6"))!;
        await store.SaveAsync(conversation);   // advances the store to version 2

        await Assert.ThrowsAsync<ConversationConcurrencyException>(() => store.SaveAsync(stale).AsTask());
    }

    [Fact]
    public async Task CompareAndSwap_RejectsAStaleWrite()
    {
        IAtomicSharedStateStore store = new InMemorySharedStateStore();
        await store.SetAsync("k", "v1");

        Assert.False(await store.TrySetIfUnchangedAsync("k", "wrong", "v2"));
        Assert.False(await store.TrySetIfUnchangedAsync("k", null, "v2"));   // "must be absent"
        Assert.True(await store.TrySetIfUnchangedAsync("k", "v1", "v2"));
        Assert.Equal("v2", await store.GetAsync("k"));
    }

    [Fact]
    public async Task CompareAndSwap_CreatesOnlyWhenAbsent()
    {
        IAtomicSharedStateStore store = new InMemorySharedStateStore();

        Assert.True(await store.TrySetIfUnchangedAsync("fresh", null, "created"));
        Assert.False(await store.TrySetIfUnchangedAsync("fresh", null, "again"));
        Assert.Equal("created", await store.GetAsync("fresh"));
    }

    private sealed class StubSummarizer(string summary) : IConversationSummarizer
    {
        public Task<string> SummarizeAsync(
            string? existingSummary,
            IReadOnlyList<ChatMessage> evictedMessages,
            CancellationToken cancellationToken = default) => Task.FromResult(summary);
    }

    /// <summary>An ISharedStateStore that deliberately does not offer compare-and-swap.</summary>
    private sealed class NonAtomicStore : ISharedStateStore
    {
        private readonly InMemorySharedStateStore _inner = new();

        public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.GetAsync(key, cancellationToken);

        public ValueTask SetAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default) =>
            _inner.SetAsync(key, value, timeToLive, cancellationToken);

        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.RemoveAsync(key, cancellationToken);

        public ValueTask<long> IncrementAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.IncrementAsync(key, cancellationToken);

        public ValueTask<long> AppendAsync(string key, string value, CancellationToken cancellationToken = default) =>
            _inner.AppendAsync(key, value, cancellationToken);

        public ValueTask<IReadOnlyList<string>> GetListAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.GetListAsync(key, cancellationToken);
    }

    private sealed class UsageClient(long inputTokens) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                Usage = new UsageDetails { InputTokenCount = inputTokens },
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
