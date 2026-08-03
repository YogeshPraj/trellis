using Microsoft.Extensions.AI;
using Trellis.State;

namespace Trellis.Tests;

public class ConversationCompactionTests
{
    private sealed class FakeSummarizer : IConversationSummarizer
    {
        public string? LastExistingSummary;
        public List<ChatMessage> LastEvicted = [];
        public int Calls;

        public Task<string> SummarizeAsync(
            string? existingSummary, IReadOnlyList<ChatMessage> evictedMessages, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastExistingSummary = existingSummary;
            LastEvicted = [.. evictedMessages];
            return Task.FromResult($"summary-v{Calls}");
        }
    }

    private static Conversation ConversationWithTurns(int count)
    {
        var conversation = new Conversation("conv");
        for (int i = 0; i < count; i++)
        {
            conversation.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"m{i}"));
        }
        return conversation;
    }

    [Fact]
    public async Task BelowBudget_NothingHappens()
    {
        var summarizer = new FakeSummarizer();
        var compactor = new ConversationCompactor(
            summarizer, options: new CompactionOptions { MaxHotMessages = 10, KeepRecentMessages = 4 });
        Conversation conversation = ConversationWithTurns(10);

        Assert.False(await compactor.CompactIfNeededAsync(conversation));
        Assert.Equal(0, summarizer.Calls);
        Assert.Null(conversation.Summary);
        Assert.Equal(0, conversation.ContextEpoch);
    }

    [Fact]
    public async Task OverBudget_EvictsOldTurns_KeepsRecentHot_ArchivesCold()
    {
        var summarizer = new FakeSummarizer();
        var archive = new InMemoryConversationArchive();
        var compactor = new ConversationCompactor(
            summarizer, archive, new CompactionOptions { MaxHotMessages = 10, KeepRecentMessages = 4 });
        Conversation conversation = ConversationWithTurns(11);

        Assert.True(await compactor.CompactIfNeededAsync(conversation));

        // Hot: the 4 most recent turns stay verbatim.
        Assert.Equal(4, conversation.Messages.Count);
        Assert.Equal("m7", conversation.Messages[0].Text);
        Assert.Equal("m10", conversation.Messages[^1].Text);

        // Cold: the 7 oldest went through the summarizer and into the archive, in order.
        Assert.Equal(7, summarizer.LastEvicted.Count);
        Assert.Equal("summary-v1", conversation.Summary);
        Assert.Equal(7, conversation.ArchivedCount);
        Assert.Equal(1, conversation.ContextEpoch);
        IReadOnlyList<ChatMessage> cold = await archive.LoadAsync("conv");
        Assert.Equal(["m0", "m1", "m2", "m3", "m4", "m5", "m6"], cold.Select(m => m.Text));
    }

    [Fact]
    public async Task SecondCompaction_FeedsPreviousSummaryIn_AndAppendsToArchive()
    {
        var summarizer = new FakeSummarizer();
        var archive = new InMemoryConversationArchive();
        var compactor = new ConversationCompactor(
            summarizer, archive, new CompactionOptions { MaxHotMessages = 6, KeepRecentMessages = 2 });
        Conversation conversation = ConversationWithTurns(7);

        await compactor.CompactIfNeededAsync(conversation);
        for (int i = 7; i < 14; i++)
        {
            conversation.Add(new ChatMessage(ChatRole.User, $"m{i}"));
        }
        await compactor.CompactIfNeededAsync(conversation);

        Assert.Equal("summary-v1", summarizer.LastExistingSummary);   // rolling: v1 fed into v2
        Assert.Equal("summary-v2", conversation.Summary);
        Assert.Equal(2, conversation.ContextEpoch);
        Assert.Equal(12, (await archive.LoadAsync("conv")).Count);    // both evictions appended
    }

    [Fact]
    public async Task AgentRun_CompactsAndSendsSummaryPlusHotTail()
    {
        var client = new FakeChatClient("reply");
        var summarizer = new FakeSummarizer();
        var agent = new Agent(client, instructions: "Be helpful.", compactor: new ConversationCompactor(
            summarizer, options: new CompactionOptions { MaxHotMessages = 4, KeepRecentMessages = 2 }));
        var conversation = new Conversation("conv");

        for (int turn = 0; turn < 4; turn++)
        {
            await agent.RunAsync(conversation, $"question {turn}");
        }

        // Turn 4 found 6 hot messages (> 4): compaction evicted 4, kept 2 hot, then added the prompt.
        IReadOnlyList<ChatMessage> sent = client.Requests[^1];
        Assert.Equal(ChatRole.System, sent[0].Role);                      // instructions
        Assert.Equal(ChatRole.System, sent[1].Role);                      // summary of cold context
        Assert.Contains("summary-v1", sent[1].Text);
        Assert.Equal(5, sent.Count);                                      // instr + summary + 2 hot + new question
        Assert.Equal("question 3", sent[^1].Text);

        // The routing id now carries the epoch so router-side deltas invalidate.
        Assert.Equal("conv:1", client.Options[^1]!.ConversationId);
        Assert.Equal("conv:1", conversation.RoutingId);
    }

    [Fact]
    public async Task ChatClientSummarizer_FoldsExistingSummaryAndTurnsIntoPrompt()
    {
        var client = new FakeChatClient("the updated summary");
        var summarizer = new ChatClientConversationSummarizer(client);

        string result = await summarizer.SummarizeAsync(
            "previous summary",
            [new ChatMessage(ChatRole.User, "hello there"), new ChatMessage(ChatRole.Assistant, "hi!")]);

        Assert.Equal("the updated summary", result);
        string prompt = Assert.Single(client.Requests)[0].Text;
        Assert.Contains("previous summary", prompt);
        Assert.Contains("user: hello there", prompt);
        Assert.Contains("assistant: hi!", prompt);
    }

    [Fact]
    public async Task SharedStateArchive_RoundTripsThroughAnySharedStateStore()
    {
        var archive = new SharedStateConversationArchive(new InMemorySharedStateStore());

        await archive.ArchiveAsync("c1", [new ChatMessage(ChatRole.User, "old question")]);
        await archive.ArchiveAsync("c1", [new ChatMessage(ChatRole.Assistant, "old answer")]);

        IReadOnlyList<ChatMessage> cold = await archive.LoadAsync("c1");
        Assert.Equal(["old question", "old answer"], cold.Select(m => m.Text));
        Assert.Equal([ChatRole.User, ChatRole.Assistant], cold.Select(m => m.Role));
        Assert.Empty(await archive.LoadAsync("unknown"));
    }

    [Fact]
    public void InvalidOptions_Throw()
    {
        Assert.Throws<ArgumentException>(() => new ConversationCompactor(
            new FakeSummarizer(),
            options: new CompactionOptions { MaxHotMessages = 5, KeepRecentMessages = 5 }));
    }
}
