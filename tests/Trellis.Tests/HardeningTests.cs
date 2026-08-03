using Microsoft.Extensions.AI;
using Trellis.Graph;
using Trellis.Routing;

namespace Trellis.Tests;

public class CompactionHardeningTests
{
    private sealed class FakeSummarizer : IConversationSummarizer
    {
        public Exception? Throw;
        public int Calls;

        public Task<string> SummarizeAsync(
            string? existingSummary, IReadOnlyList<ChatMessage> evictedMessages, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Throw is not null ? Task.FromException<string>(Throw) : Task.FromResult("summary");
        }
    }

    [Fact]
    public async Task SummarizerFailure_NeverFailsTheTurn_AndReportsViaCallback()
    {
        Exception? reported = null;
        var summarizer = new FakeSummarizer { Throw = new HttpRequestException("summarizer model down") };
        var compactor = new ConversationCompactor(
            summarizer,
            options: new CompactionOptions
            {
                MaxHotMessages = 4,
                KeepRecentMessages = 2,
                OnCompactionFailure = ex => reported = ex,
            });
        var agent = new Agent(new FakeChatClient("reply"), compactor: compactor);
        var conversation = new Conversation("c");

        // Enough turns to trigger compaction repeatedly; none of them may throw.
        for (int turn = 0; turn < 5; turn++)
        {
            await agent.RunAsync(conversation, $"q{turn}");
        }

        Assert.NotNull(reported);
        Assert.Null(conversation.Summary);                     // conversation untouched by the failed compaction
        Assert.Equal(0, conversation.ContextEpoch);
        Assert.True(conversation.Messages.Count > 4);          // uncompacted but alive
    }

    [Fact]
    public async Task EvictionBoundary_NeverSplitsToolCallChains()
    {
        var summarizer = new FakeSummarizer();
        var compactor = new ConversationCompactor(
            summarizer, options: new CompactionOptions { MaxHotMessages = 4, KeepRecentMessages = 2 });

        // History: u0, a-call, tool-result, a-answer, u1, a1  (6 messages, boundary would be 4)
        var conversation = new Conversation("c");
        conversation.Add(new ChatMessage(ChatRole.User, "u0"));
        conversation.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call1", "get_weather")]));
        conversation.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call1", "sunny")]));
        conversation.Add(new ChatMessage(ChatRole.Assistant, "it is sunny"));
        conversation.Add(new ChatMessage(ChatRole.User, "u1"));
        conversation.Add(new ChatMessage(ChatRole.Assistant, "a1"));

        // Naive boundary (count 6 - keep 2 = 4) already lands after the chain here; force the
        // split case instead: keep 4 → boundary 2 = the tool-result message.
        var splitCompactor = new ConversationCompactor(
            summarizer, options: new CompactionOptions { MaxHotMessages = 5, KeepRecentMessages = 4 });

        Assert.True(await splitCompactor.CompactIfNeededAsync(conversation));

        // Boundary advanced past the tool result: hot history starts with a valid message.
        Assert.DoesNotContain(
            conversation.Messages[0].Contents, c => c is FunctionResultContent);
        Assert.Equal("it is sunny", conversation.Messages[0].Text);
    }

    [Fact]
    public async Task Compaction_RunsOffTheResponsePath()
    {
        var gate = new TaskCompletionSource<string>();
        var summarizer = new GatedSummarizer(gate.Task);
        var compactor = new ConversationCompactor(
            summarizer, options: new CompactionOptions { MaxHotMessages = 2, KeepRecentMessages = 1 });
        var agent = new Agent(new FakeChatClient("reply"), compactor: compactor);
        var conversation = new Conversation("c");

        await agent.RunAsync(conversation, "q0");   // 2 messages after this turn
        await agent.RunAsync(conversation, "q1");   // 4 > 2 → compaction starts AFTER this returns

        // The turn returned while the summarizer is still hanging — latency is off the path.
        Assert.False(conversation.PendingCompaction!.IsCompleted);

        gate.SetResult("summary");
        await conversation.PendingCompaction;
        Assert.Equal(1, conversation.ContextEpoch);
    }

    private sealed class GatedSummarizer(Task<string> result) : IConversationSummarizer
    {
        public async Task<string> SummarizeAsync(
            string? existingSummary, IReadOnlyList<ChatMessage> evictedMessages, CancellationToken cancellationToken = default) =>
            await result;
    }
}

public class ClassifierPrecisionTests
{
    private readonly DefaultFailureClassifier _classifier = new();

    [Theory]
    [InlineData("the request took 500ms to complete")]
    [InlineData("processed 429000 rows")]
    [InlineData("order id 15022 not found")]
    public void NumbersInsideWords_DoNotTripEndpoints(string message)
    {
        Assert.Equal(FailureKind.Unknown, _classifier.Classify(new InvalidOperationException(message)).Kind);
    }

    [Theory]
    [InlineData("HTTP 500 Internal Server Error", FailureKind.ServerError)]
    [InlineData("status: 429", FailureKind.RateLimit)]
    [InlineData("upstream returned 503", FailureKind.ServerError)]
    public void StandaloneStatusCodes_StillClassify(string message, FailureKind expected)
    {
        Assert.Equal(expected, _classifier.Classify(new InvalidOperationException(message)).Kind);
    }
}

public class GraphRunGuardTests
{
    private sealed record S(int N);

    [Fact]
    public async Task ConcurrentRuns_OnSameThreadId_AreRejected()
    {
        var gate = new TaskCompletionSource();
        CompiledGraph<S> graph = new StateGraph<S>()
            .AddNode("wait", async (s, _) => { await gate.Task; return s; })
            .SetEntryPoint("wait")
            .Compile(new InMemoryCheckpointer<S>());
        var options = new GraphRunOptions { ThreadId = "t1" };

        Task<GraphResult<S>> first = graph.RunAsync(new S(0), options);
        await Task.Delay(50);

        await Assert.ThrowsAsync<GraphExecutionException>(() => graph.RunAsync(new S(0), options));

        gate.SetResult();
        await first;

        // After the first run completes, the thread id is free again.
        GraphResult<S> rerun = await graph.RunAsync(new S(0), options);
        Assert.Equal(GraphRunStatus.Completed, rerun.Status);
    }
}

public class SqliteRetentionTests : IDisposable
{
    private sealed record S(int N);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"trellis-retention-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task History_IsPrunedToRetentionLimit()
    {
        var checkpointer = Trellis.Checkpointing.Sqlite.SqliteCheckpointer<S>.FromFile(_dbPath, maxCheckpointsPerThread: 5);

        for (int i = 1; i <= 12; i++)
        {
            await checkpointer.SaveAsync(new Checkpoint<S>("t1", i, "next", new S(i)));
        }

        IReadOnlyList<Checkpoint<S>> history = await checkpointer.GetHistoryAsync("t1");
        Assert.Equal(5, history.Count);
        Assert.Equal([8, 9, 10, 11, 12], history.Select(c => c.Step));

        // Latest is still correct after pruning.
        Assert.Equal(12, (await checkpointer.LoadAsync("t1"))!.Step);
    }
}
