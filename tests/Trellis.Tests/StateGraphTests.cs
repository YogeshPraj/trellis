using Trellis.Graph;

namespace Trellis.Tests;

public class StateGraphTests
{
    private sealed record CounterState(int Count, List<string> Visited);

    private static CounterState NewState() => new(0, []);

    private static CounterState Visit(CounterState s, string node) =>
        s with { Count = s.Count + 1, Visited = [.. s.Visited, node] };

    [Fact]
    public async Task LinearGraph_RunsNodesInOrder()
    {
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("a", s => Visit(s, "a"))
            .AddNode("b", s => Visit(s, "b"))
            .AddNode("c", s => Visit(s, "c"))
            .AddEdge("a", "b")
            .AddEdge("b", "c")
            .SetEntryPoint("a")
            .Compile();

        GraphResult<CounterState> result = await graph.RunAsync(NewState());

        Assert.Equal(["a", "b", "c"], result.FinalState.Visited);
        Assert.Equal(3, result.Steps);
    }

    [Fact]
    public async Task NodeWithoutEdge_ImplicitlyEndsGraph()
    {
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("only", s => Visit(s, "only"))
            .SetEntryPoint("only")
            .Compile();

        GraphResult<CounterState> result = await graph.RunAsync(NewState());

        Assert.Equal(["only"], result.FinalState.Visited);
    }

    [Fact]
    public async Task ConditionalEdge_LoopsUntilRouterEnds()
    {
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("tick", s => Visit(s, "tick"))
            .AddConditionalEdge("tick", s => s.Count < 3 ? "tick" : StateGraph.End)
            .SetEntryPoint("tick")
            .Compile();

        GraphResult<CounterState> result = await graph.RunAsync(NewState());

        Assert.Equal(3, result.FinalState.Count);
    }

    [Fact]
    public async Task InfiniteLoop_ThrowsGraphRecursionException()
    {
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("spin", s => s)
            .AddEdge("spin", "spin")
            .SetEntryPoint("spin")
            .Compile();

        await Assert.ThrowsAsync<GraphRecursionException>(() =>
            graph.RunAsync(NewState(), new GraphRunOptions { MaxSteps = 10 }));
    }

    [Fact]
    public async Task StreamAsync_YieldsStartCompleteAndFinalEvents()
    {
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("a", s => Visit(s, "a"))
            .AddNode("b", s => Visit(s, "b"))
            .AddEdge("a", "b")
            .SetEntryPoint("a")
            .Compile();

        List<GraphEvent<CounterState>> events = [];
        await foreach (GraphEvent<CounterState> evt in graph.StreamAsync(NewState()))
        {
            events.Add(evt);
        }

        Assert.Equal(
            [
                GraphEventType.NodeStarted,
                GraphEventType.NodeCompleted,
                GraphEventType.NodeStarted,
                GraphEventType.NodeCompleted,
                GraphEventType.GraphCompleted,
            ],
            events.Select(e => e.Type));
        Assert.Equal("b", events[1].Next);
        Assert.Equal(StateGraph.End, events[3].Next);
    }

    [Fact]
    public async Task Checkpointer_RecordsEveryStep()
    {
        var checkpointer = new InMemoryCheckpointer<CounterState>();
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("a", s => Visit(s, "a"))
            .AddNode("b", s => Visit(s, "b"))
            .AddEdge("a", "b")
            .SetEntryPoint("a")
            .Compile(checkpointer);

        GraphResult<CounterState> result = await graph.RunAsync(NewState());

        IReadOnlyList<Checkpoint<CounterState>> history = await checkpointer.GetHistoryAsync(result.ThreadId);
        Assert.Equal(2, history.Count);
        Assert.Equal("b", history[0].NextNode);
        Assert.Equal(StateGraph.End, history[^1].NextNode);
    }

    [Fact]
    public async Task Resume_SkipsAlreadyCompletedNodes()
    {
        var checkpointer = new InMemoryCheckpointer<CounterState>();
        // Simulate a run that already finished node "a" and is about to run "b".
        await checkpointer.SaveAsync(new Checkpoint<CounterState>("t1", 1, "b", Visit(NewState(), "a")));

        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("a", s => Visit(s, "a"))
            .AddNode("b", s => Visit(s, "b"))
            .AddEdge("a", "b")
            .SetEntryPoint("a")
            .Compile(checkpointer);

        GraphResult<CounterState> result = await graph.RunAsync(
            NewState(), new GraphRunOptions { ThreadId = "t1" });

        Assert.Equal(["a", "b"], result.FinalState.Visited);
        Assert.Equal(2, result.Steps);
    }

    [Fact]
    public void Compile_WithoutEntryPoint_Throws()
    {
        StateGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("a", s => s);

        Assert.Throws<GraphDefinitionException>(() => graph.Compile());
    }

    [Fact]
    public void Edge_ToUnknownNode_ThrowsAtCompile()
    {
        StateGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("a", s => s)
            .AddEdge("a", "ghost")
            .SetEntryPoint("a");

        Assert.Throws<GraphDefinitionException>(() => graph.Compile());
    }

    [Fact]
    public async Task Router_ReturningUnknownNode_ThrowsAtRuntime()
    {
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("a", s => s)
            .AddConditionalEdge("a", _ => "ghost")
            .SetEntryPoint("a")
            .Compile();

        await Assert.ThrowsAsync<GraphExecutionException>(() => graph.RunAsync(NewState()));
    }
}
