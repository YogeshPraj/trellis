using Microsoft.Extensions.AI;

namespace Trellis.Tests;

public class TokenBudgetCompactionTests
{
    private sealed class StubSummarizer : IConversationSummarizer
    {
        public List<IReadOnlyList<ChatMessage>> Evicted { get; } = [];

        public Task<string> SummarizeAsync(
            string? existingSummary,
            IReadOnlyList<ChatMessage> evictedMessages,
            CancellationToken cancellationToken = default)
        {
            Evicted.Add(evictedMessages);
            return Task.FromResult("SUMMARY");
        }
    }

    /// <summary>One token per character, no framing overhead — makes budgets exact in tests.</summary>
    private sealed class OneTokenPerCharCounter : ITokenCounter
    {
        public int CountTokens(ChatMessage message) => message.Text.Length;
    }

    private static Conversation ConversationOf(params string[] texts)
    {
        var conversation = new Conversation();
        foreach (string text in texts)
        {
            conversation.Add(new ChatMessage(ChatRole.User, text));
        }
        return conversation;
    }

    [Fact]
    public async Task UnderBothBudgets_DoesNotCompact()
    {
        var summarizer = new StubSummarizer();
        var compactor = new ConversationCompactor(summarizer, options: new CompactionOptions
        {
            MaxHotMessages = 100,
            KeepRecentMessages = 10,
            MaxHotTokens = 100,
            TokenCounter = new OneTokenPerCharCounter(),
        });

        bool compacted = await compactor.CompactIfNeededAsync(ConversationOf("aaa", "bbb"));

        Assert.False(compacted);
        Assert.Empty(summarizer.Evicted);
    }

    [Fact]
    public async Task OverTokenBudget_CompactsEvenWhenMessageCountIsFine()
    {
        var summarizer = new StubSummarizer();
        var compactor = new ConversationCompactor(summarizer, options: new CompactionOptions
        {
            MaxHotMessages = 100,           // nowhere near tripping
            KeepRecentMessages = 50,
            MaxHotTokens = 30,
            KeepRecentTokens = 10,
            TokenCounter = new OneTokenPerCharCounter(),
        });
        // 4 messages x 10 chars = 40 tokens > 30.
        Conversation conversation = ConversationOf("aaaaaaaaaa", "bbbbbbbbbb", "cccccccccc", "dddddddddd");

        bool compacted = await compactor.CompactIfNeededAsync(conversation);

        Assert.True(compacted);
        // Keep budget of 10 fits exactly the newest message.
        Assert.Single(conversation.Messages);
        Assert.Equal("dddddddddd", conversation.Messages[0].Text);
        Assert.Equal("SUMMARY", conversation.Summary);
        Assert.Equal(3, conversation.ArchivedCount);
    }

    [Fact]
    public async Task KeepRecentTokens_DefaultsToAThirdOfTheBudget()
    {
        var compactor = new ConversationCompactor(new StubSummarizer(), options: new CompactionOptions
        {
            MaxHotMessages = 100,
            KeepRecentMessages = 50,
            MaxHotTokens = 30,              // default keep budget = 10
            TokenCounter = new OneTokenPerCharCounter(),
        });
        Conversation conversation = ConversationOf("aaaaa", "bbbbb", "ccccc", "ddddd", "eeeee", "fffff", "ggggg");

        Assert.True(await compactor.CompactIfNeededAsync(conversation));

        // 2 messages x 5 tokens fits in 10; a third would exceed it.
        Assert.Equal(2, conversation.Messages.Count);
        Assert.Equal("fffff", conversation.Messages[0].Text);
    }

    [Fact]
    public async Task StricterOfTheTwoBudgetsWins()
    {
        var compactor = new ConversationCompactor(new StubSummarizer(), options: new CompactionOptions
        {
            MaxHotMessages = 4,             // would keep 3
            KeepRecentMessages = 3,
            MaxHotTokens = 10,              // would keep 1 (keep budget 3 → only "eeee" is too big, keep newest)
            KeepRecentTokens = 4,
            TokenCounter = new OneTokenPerCharCounter(),
        });
        Conversation conversation = ConversationOf("aaaa", "bbbb", "cccc", "dddd", "eeee");

        Assert.True(await compactor.CompactIfNeededAsync(conversation));

        Assert.Single(conversation.Messages);
        Assert.Equal("eeee", conversation.Messages[0].Text);
    }

    [Fact]
    public async Task ProviderReportedUsage_TripsTheBudget_EvenWhenTheEstimatorWouldNot()
    {
        // The hot messages are tiny, but the provider reports a huge prompt — long
        // instructions, images, framing the counter cannot see. The budget must still bite.
        var summarizer = new StubSummarizer();
        var compactor = new ConversationCompactor(summarizer, options: new CompactionOptions
        {
            MaxHotMessages = 1000,          // message budget nowhere near tripping
            KeepRecentMessages = 100,
            MaxHotTokens = 500,
            KeepRecentTokens = 100,
            TokenCounter = new OneTokenPerCharCounter(),
        });
        var agent = new Agent(new UsageReportingChatClient(inputTokens: 9_000));
        var conversation = new Conversation();

        await agent.RunAsync(conversation, "hi");
        Assert.Equal(9_000, conversation.LastInputTokenCount);

        Assert.True(await compactor.CompactIfNeededAsync(conversation));
        Assert.Equal("SUMMARY", conversation.Summary);
        // Unattributable overhead (8_996 tokens) swamps the keep budget, so only the
        // newest message survives — the honest response to "history isn't the problem".
        Assert.Single(conversation.Messages);
    }

    [Fact]
    public async Task WithoutReportedUsage_TheEstimatorAloneDrivesTheBudget()
    {
        var compactor = new ConversationCompactor(new StubSummarizer(), options: new CompactionOptions
        {
            MaxHotMessages = 1000,
            KeepRecentMessages = 100,
            MaxHotTokens = 20,
            KeepRecentTokens = 10,
            TokenCounter = new OneTokenPerCharCounter(),
        });
        // FakeChatClient reports no usage at all, so LastInputTokenCount stays null.
        var agent = new Agent(new FakeChatClient("aaaaaaaaaa"));
        var conversation = new Conversation();
        await agent.RunAsync(conversation, "bbbbbbbbbb");
        await agent.RunAsync(conversation, "cccccccccc");

        Assert.Null(conversation.LastInputTokenCount);
        Assert.True(await compactor.CompactIfNeededAsync(conversation));
        Assert.Single(conversation.Messages);
    }

    [Fact]
    public async Task RunAsync_RecordsProviderReportedInputTokens()
    {
        var client = new UsageReportingChatClient(inputTokens: 1234);
        var agent = new Agent(client);
        var conversation = new Conversation();

        await agent.RunAsync(conversation, "hi");

        Assert.Equal(1234, conversation.LastInputTokenCount);
    }

    [Fact]
    public async Task Streaming_RecordsProviderReportedInputTokens()
    {
        var client = new UsageReportingChatClient(inputTokens: 4321);
        var agent = new Agent(client);
        var conversation = new Conversation();

        await foreach (ChatResponseUpdate _ in agent.RunStreamingAsync(conversation, "hi"))
        {
        }

        Assert.Equal(4321, conversation.LastInputTokenCount);
    }

    [Fact]
    public async Task TokenBudget_NeverSplitsAToolCallChain()
    {
        var compactor = new ConversationCompactor(new StubSummarizer(), options: new CompactionOptions
        {
            MaxHotMessages = 100,
            KeepRecentMessages = 50,
            MaxHotTokens = 5,
            KeepRecentTokens = 4,
            TokenCounter = new OneTokenPerCharCounter(),
        });
        var conversation = new Conversation();
        conversation.Add(new ChatMessage(ChatRole.User, "question"));
        conversation.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("id1", "tool", null)]));
        conversation.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("id1", "result")]));
        conversation.Add(new ChatMessage(ChatRole.Assistant, "answer"));

        Assert.True(await compactor.CompactIfNeededAsync(conversation));

        // The retained tail must never begin with an orphaned tool result.
        Assert.DoesNotContain(
            conversation.Messages[0].Contents,
            c => c is FunctionResultContent);
    }

    [Fact]
    public void KeepRecentTokens_WithoutMaxHotTokens_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new ConversationCompactor(
            new StubSummarizer(),
            options: new CompactionOptions { KeepRecentTokens = 10 }));
    }

    [Fact]
    public void KeepRecentTokens_NotSmallerThanMaxHotTokens_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new ConversationCompactor(
            new StubSummarizer(),
            options: new CompactionOptions { MaxHotTokens = 10, KeepRecentTokens = 10 }));
    }

    [Fact]
    public async Task Summarizer_TruncatesSummariesThatBlowTheCeiling()
    {
        var verbose = new FakeChatClient(new string('x', 5_000));
        var summarizer = new ChatClientConversationSummarizer(verbose, maxSummaryCharacters: 100);

        string summary = await summarizer.SummarizeAsync(null, [new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal(100, summary.Length);
    }

    [Fact]
    public async Task Summarizer_TellsTheModelItsBudget()
    {
        var client = new FakeChatClient("short summary");
        var summarizer = new ChatClientConversationSummarizer(client, maxSummaryCharacters: 250);

        await summarizer.SummarizeAsync(null, [new ChatMessage(ChatRole.User, "hi")]);

        Assert.Contains("250", Assert.Single(client.Requests)[0].Text);
    }

    [Fact]
    public void HeuristicCounter_CountsToolTrafficNotJustText()
    {
        ITokenCounter counter = new HeuristicTokenCounter();
        var text = new ChatMessage(ChatRole.User, "");
        var call = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("id", "search_flights", new Dictionary<string, object?> { ["city"] = "Pune" })]);

        Assert.True(counter.CountTokens(call) > counter.CountTokens(text),
            "tool calls cost prompt tokens even though they carry no Text");
    }

    private sealed class UsageReportingChatClient(long inputTokens) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                Usage = new UsageDetails { InputTokenCount = inputTokens },
            });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            yield return new ChatResponseUpdate
            {
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = inputTokens })],
            };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
