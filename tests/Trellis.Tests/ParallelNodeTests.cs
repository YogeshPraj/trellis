using Trellis.Graph;

namespace Trellis.Tests;

public class ParallelNodeTests
{
    private sealed record FanState(List<string> Results);

    [Fact]
    public async Task Branches_RunConcurrently_AndMerge()
    {
        var gate = new TaskCompletionSource();
        int running = 0;

        // Each branch waits until both have started — deadlocks unless truly concurrent.
        async Task<FanState> Branch(FanState s, string label)
        {
            if (Interlocked.Increment(ref running) == 2)
            {
                gate.TrySetResult();
            }
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return new FanState([label]);
        }

        CompiledGraph<FanState> graph = new StateGraph<FanState>()
            .AddParallelNode(
                "fan",
                branches: [s => Branch(s, "left"), s => Branch(s, "right")],
                merge: (input, results) => new FanState([.. results.SelectMany(r => r.Results).Order()]))
            .SetEntryPoint("fan")
            .Compile();

        GraphResult<FanState> result = await graph.RunAsync(new FanState([]));

        Assert.Equal(["left", "right"], result.FinalState.Results);
    }

    [Fact]
    public async Task Merge_ReceivesInputState_AndAllBranchResults()
    {
        CompiledGraph<FanState> graph = new StateGraph<FanState>()
            .AddParallelNode(
                "fan",
                branches:
                [
                    (Func<FanState, Task<FanState>>)(s => Task.FromResult(new FanState(["a"]))),
                    s => Task.FromResult(new FanState(["b"])),
                    s => Task.FromResult(new FanState(["c"])),
                ],
                merge: (input, results) =>
                {
                    Assert.Equal(["seed"], input.Results);
                    return new FanState([.. input.Results, .. results.SelectMany(r => r.Results)]);
                })
            .SetEntryPoint("fan")
            .Compile();

        GraphResult<FanState> result = await graph.RunAsync(new FanState(["seed"]));

        Assert.Equal(["seed", "a", "b", "c"], result.FinalState.Results);
    }

    [Fact]
    public void ParallelNode_WithNoBranches_Throws()
    {
        Assert.Throws<GraphDefinitionException>(() =>
            new StateGraph<FanState>().AddParallelNode(
                "fan",
                branches: Array.Empty<NodeHandler<FanState>>(),
                merge: (input, results) => input));
    }
}
