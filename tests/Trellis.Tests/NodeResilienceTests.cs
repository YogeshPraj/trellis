using Trellis.Graph;

namespace Trellis.Tests;

public class NodeResilienceTests
{
    private sealed record CounterState(int Value, string? Note = null);

    private static NodeResilience<CounterState> NoDelayRetry(int maxAttempts, Func<Exception, bool>? shouldRetry = null) =>
        new()
        {
            Retry = new ExponentialBackoffRetryPolicy(
                maxAttempts, baseDelay: TimeSpan.Zero, jitterFactor: 0, shouldRetry: shouldRetry),
        };

    [Fact]
    public async Task FlakyNode_SucceedsOnRetry()
    {
        int calls = 0;
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("flaky", s =>
            {
                calls++;
                return calls < 3 ? throw new InvalidOperationException("boom") : s with { Value = 42 };
            }, NoDelayRetry(maxAttempts: 3))
            .SetEntryPoint("flaky")
            .Compile();

        GraphResult<CounterState> result = await graph.RunAsync(new CounterState(0));

        Assert.Equal(42, result.FinalState.Value);
        Assert.Equal(3, calls);
        Assert.Equal(1, result.Steps); // retries are re-executions of one step, not extra steps
    }

    [Fact]
    public async Task WithoutAPolicy_TheFirstFailurePropagates()
    {
        int calls = 0;
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("boom", CounterState (s) => { calls++; throw new InvalidOperationException("boom"); })
            .SetEntryPoint("boom")
            .Compile();

        await Assert.ThrowsAsync<InvalidOperationException>(() => graph.RunAsync(new CounterState(0)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExhaustedRetries_RethrowTheOriginalException()
    {
        int calls = 0;
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("boom", CounterState (s) => { calls++; throw new InvalidOperationException("always"); },
                NoDelayRetry(maxAttempts: 4))
            .SetEntryPoint("boom")
            .Compile();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => graph.RunAsync(new CounterState(0)));

        Assert.Equal("always", ex.Message);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Fallback_RecoversAfterRetriesAreExhausted()
    {
        int calls = 0;
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("boom", CounterState (s) => { calls++; throw new InvalidOperationException("down"); },
                new NodeResilience<CounterState>
                {
                    Retry = new ExponentialBackoffRetryPolicy(2, baseDelay: TimeSpan.Zero, jitterFactor: 0),
                    Fallback = (state, error, _) => Task.FromResult(state with { Note = $"degraded: {error.Message}" }),
                })
            .SetEntryPoint("boom")
            .Compile();

        GraphResult<CounterState> result = await graph.RunAsync(new CounterState(7));

        Assert.Equal(2, calls);
        Assert.Equal("degraded: down", result.FinalState.Note);
        Assert.Equal(7, result.FinalState.Value);
        Assert.Equal(GraphRunStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Fallback_WithoutRetryPolicy_RunsOnFirstFailure()
    {
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("boom", CounterState (s) => throw new InvalidOperationException("nope"),
                new NodeResilience<CounterState> { Fallback = (s, _, _) => Task.FromResult(s with { Note = "fell back" }) })
            .SetEntryPoint("boom")
            .Compile();

        GraphResult<CounterState> result = await graph.RunAsync(new CounterState(1));

        Assert.Equal("fell back", result.FinalState.Note);
    }

    [Fact]
    public async Task FailingFallback_SurfacesBothErrors()
    {
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("boom", CounterState (s) => throw new InvalidOperationException("original"),
                new NodeResilience<CounterState>
                {
                    Fallback = (_, _, _) => throw new NotSupportedException("fallback failed"),
                })
            .SetEntryPoint("boom")
            .Compile();

        var ex = await Assert.ThrowsAsync<GraphExecutionException>(() => graph.RunAsync(new CounterState(0)));

        var aggregate = Assert.IsType<AggregateException>(ex.InnerException);
        Assert.Collection(aggregate.InnerExceptions,
            first => Assert.Equal("original", first.Message),
            second => Assert.Equal("fallback failed", second.Message));
    }

    [Fact]
    public async Task ShouldRetryFilter_SkipsRetriesForHopelessErrors()
    {
        int calls = 0;
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("boom", CounterState (s) => { calls++; throw new ArgumentException("bad input"); },
                NoDelayRetry(maxAttempts: 5, shouldRetry: e => e is not ArgumentException))
            .SetEntryPoint("boom")
            .Compile();

        await Assert.ThrowsAsync<ArgumentException>(() => graph.RunAsync(new CounterState(0)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task StreamAsync_EmitsRetryAndFallbackEvents()
    {
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("boom", CounterState (s) => throw new InvalidOperationException("nope"),
                new NodeResilience<CounterState>
                {
                    Retry = new ExponentialBackoffRetryPolicy(3, baseDelay: TimeSpan.Zero, jitterFactor: 0),
                    Fallback = (s, _, _) => Task.FromResult(s with { Note = "recovered" }),
                })
            .SetEntryPoint("boom")
            .Compile();

        var events = new List<GraphEvent<CounterState>>();
        await foreach (GraphEvent<CounterState> evt in graph.StreamAsync(new CounterState(0)))
        {
            events.Add(evt);
        }

        List<GraphEvent<CounterState>> retries = [.. events.Where(e => e.Type == GraphEventType.NodeRetrying)];
        Assert.Equal(2, retries.Count);                       // 3 attempts = 2 retries
        Assert.Equal([1, 2], retries.Select(r => r.Attempt));
        Assert.All(retries, r => Assert.IsType<InvalidOperationException>(r.Error));

        GraphEvent<CounterState> fallback = Assert.Single(events, e => e.Type == GraphEventType.NodeFallbackApplied);
        Assert.Equal("recovered", fallback.State.Note);
        Assert.NotNull(fallback.Error);
    }

    [Fact]
    public async Task Cancellation_IsNeverRetried()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("cancel", CounterState (s) =>
            {
                calls++;
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
                return s;
            }, NoDelayRetry(maxAttempts: 5))
            .SetEntryPoint("cancel")
            .Compile();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => graph.RunAsync(new CounterState(0), cancellationToken: cts.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RetriedNode_CheckpointsOnceOnSuccess()
    {
        int calls = 0;
        var checkpointer = new InMemoryCheckpointer<CounterState>();
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("flaky", s =>
            {
                calls++;
                return calls < 3 ? throw new InvalidOperationException("boom") : s with { Value = 1 };
            }, NoDelayRetry(maxAttempts: 3))
            .SetEntryPoint("flaky")
            .Compile(checkpointer);

        await graph.RunAsync(new CounterState(0), new GraphRunOptions { ThreadId = "t1" });

        // Failed attempts must not leave checkpoints behind: one node, one checkpoint.
        Assert.Single(await checkpointer.GetHistoryAsync("t1"));
    }

    [Fact]
    public async Task ParallelNode_RetriesTheWholeNode()
    {
        int branchCalls = 0;
        int flakyCalls = 0;
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddParallelNode("fan",
                branches:
                [
                    s => { branchCalls++; return Task.FromResult(s); },
                    s =>
                    {
                        flakyCalls++;
                        return flakyCalls < 2
                            ? throw new InvalidOperationException("branch down")
                            : Task.FromResult(s with { Value = 5 });
                    },
                ],
                merge: (_, results) => results[1],
                resilience: NoDelayRetry(maxAttempts: 3))
            .SetEntryPoint("fan")
            .Compile();

        GraphResult<CounterState> result = await graph.RunAsync(new CounterState(0));

        Assert.Equal(5, result.FinalState.Value);
        Assert.Equal(2, branchCalls); // the healthy branch re-ran too — documented behavior
    }

    [Fact]
    public async Task BackoffPolicy_StopsAtMaxAttempts_AndGrowsTheDelay()
    {
        var policy = new ExponentialBackoffRetryPolicy(
            maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(100), jitterFactor: 0);
        var error = new InvalidOperationException();

        NodeRetryDecision first = await policy.EvaluateAsync(new NodeFailureContext("n", 1, error));
        NodeRetryDecision second = await policy.EvaluateAsync(new NodeFailureContext("n", 2, error));
        NodeRetryDecision third = await policy.EvaluateAsync(new NodeFailureContext("n", 3, error));

        Assert.True(first.ShouldRetry);
        Assert.Equal(TimeSpan.FromMilliseconds(100), first.Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(200), second.Delay);
        Assert.False(third.ShouldRetry);
    }

    [Fact]
    public async Task BackoffPolicy_ClampsToMaxDelay()
    {
        var policy = new ExponentialBackoffRetryPolicy(
            maxAttempts: 100,
            baseDelay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromSeconds(5),
            jitterFactor: 0);

        NodeRetryDecision decision = await policy.EvaluateAsync(
            new NodeFailureContext("n", 20, new InvalidOperationException()));

        Assert.Equal(TimeSpan.FromSeconds(5), decision.Delay);
    }

    [Fact]
    public void BackoffPolicy_RejectsNonsenseConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialBackoffRetryPolicy(maxAttempts: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialBackoffRetryPolicy(jitterFactor: 1));
    }
}
