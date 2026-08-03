using Trellis.Graph;

namespace Trellis.Tests;

public class InterruptTests
{
    private sealed record ApprovalState(string Draft, bool Sent);

    private static CompiledGraph<ApprovalState> BuildGraph(ICheckpointer<ApprovalState> checkpointer) =>
        new StateGraph<ApprovalState>()
            .AddNode("draft", s => s with { Draft = "generated draft" })
            .AddNode("send", s => s with { Sent = true })
            .AddEdge("draft", "send")
            .SetEntryPoint("draft")
            .Compile(checkpointer);

    [Fact]
    public async Task InterruptBefore_PausesWithoutRunningNode()
    {
        CompiledGraph<ApprovalState> graph = BuildGraph(new InMemoryCheckpointer<ApprovalState>());

        GraphResult<ApprovalState> result = await graph.RunAsync(
            new ApprovalState("", false),
            new GraphRunOptions { ThreadId = "t1", InterruptBefore = ["send"] });

        Assert.Equal(GraphRunStatus.Interrupted, result.Status);
        Assert.Equal("send", result.InterruptedBefore);
        Assert.False(result.FinalState.Sent);
        Assert.Equal("generated draft", result.FinalState.Draft);
    }

    [Fact]
    public async Task Resume_AfterHumanEdit_RunsPendingNodeWithEditedState()
    {
        var checkpointer = new InMemoryCheckpointer<ApprovalState>();
        CompiledGraph<ApprovalState> graph = BuildGraph(checkpointer);
        var options = new GraphRunOptions { ThreadId = "t1", InterruptBefore = ["send"] };

        await graph.RunAsync(new ApprovalState("", false), options);

        // The human reviews and edits the draft while the run is paused...
        await graph.UpdateStateAsync("t1", s => s with { Draft = "human-approved draft" });

        // ...then the same thread id resumes and finishes.
        GraphResult<ApprovalState> resumed = await graph.RunAsync(new ApprovalState("", false), options);

        Assert.Equal(GraphRunStatus.Completed, resumed.Status);
        Assert.True(resumed.FinalState.Sent);
        Assert.Equal("human-approved draft", resumed.FinalState.Draft);
    }

    [Fact]
    public async Task Interrupt_WithoutCheckpointer_Throws()
    {
        CompiledGraph<ApprovalState> graph = new StateGraph<ApprovalState>()
            .AddNode("draft", s => s)
            .SetEntryPoint("draft")
            .Compile();

        await Assert.ThrowsAsync<GraphExecutionException>(() => graph.RunAsync(
            new ApprovalState("", false),
            new GraphRunOptions { ThreadId = "t1", InterruptBefore = ["draft"] }));
    }

    [Fact]
    public async Task Interrupt_WithoutThreadId_Throws()
    {
        CompiledGraph<ApprovalState> graph = BuildGraph(new InMemoryCheckpointer<ApprovalState>());

        await Assert.ThrowsAsync<GraphExecutionException>(() => graph.RunAsync(
            new ApprovalState("", false),
            new GraphRunOptions { InterruptBefore = ["send"] }));
    }
}
