using Microsoft.Extensions.Time.Testing;
using Trellis.Graph;
using Trellis.State;

namespace Trellis.Tests;

/// <summary>
/// Exclusion across instances, and what happens when it is taken away mid-run. These are the
/// scenarios a per-process guard silently failed: two instances both believing they own a
/// thread, and a stalled instance waking up to write state it no longer owns.
/// </summary>
public class RunLeaseTests
{
    private sealed record S(int N);

    /// <summary>A lease whose grants and losses the test drives by hand.</summary>
    private sealed class ControllableLease : IRunLease
    {
        private readonly long _token;

        public ControllableLease(long token = 1) => _token = token;

        public bool Grant { get; set; } = true;

        public int AcquireAttempts { get; private set; }

        public int Released { get; private set; }

        public Handle? Current { get; private set; }

        public ValueTask<RunLeaseHandle?> TryAcquireAsync(string threadId, CancellationToken cancellationToken = default)
        {
            AcquireAttempts++;
            if (!Grant)
            {
                return ValueTask.FromResult<RunLeaseHandle?>(null);
            }
            Current = new Handle(this, threadId, _token);
            return ValueTask.FromResult<RunLeaseHandle?>(Current);
        }

        internal sealed class Handle(ControllableLease owner, string threadId, long token)
            : RunLeaseHandle(threadId, token)
        {
            private readonly CancellationTokenSource _lost = new();

            public override CancellationToken Lost => _lost.Token;

            public void Lose() => _lost.Cancel();

            public override ValueTask DisposeAsync()
            {
                owner.Released++;
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Records what fencing token each save carried.</summary>
    private sealed class RecordingCheckpointer : IFencedCheckpointer<S>
    {
        private readonly InMemoryCheckpointer<S> _inner = new();

        public List<long> Fences { get; } = [];

        public Task SaveAsync(Checkpoint<S> checkpoint, CancellationToken cancellationToken = default)
        {
            Fences.Add(RunLeaseHandle.Unfenced);
            return _inner.SaveAsync(checkpoint, cancellationToken);
        }

        public Task SaveFencedAsync(Checkpoint<S> checkpoint, long fencingToken, CancellationToken cancellationToken = default)
        {
            Fences.Add(fencingToken);
            return _inner.SaveFencedAsync(checkpoint, fencingToken, cancellationToken);
        }

        public Task<Checkpoint<S>?> LoadAsync(string threadId, CancellationToken cancellationToken = default) =>
            _inner.LoadAsync(threadId, cancellationToken);

        public Task<IReadOnlyList<Checkpoint<S>>> GetHistoryAsync(string threadId, CancellationToken cancellationToken = default) =>
            _inner.GetHistoryAsync(threadId, cancellationToken);
    }

    /// <summary>A checkpointer with no fencing capability, to exercise the degrade path.</summary>
    private sealed class PlainCheckpointer : ICheckpointer<S>
    {
        private readonly InMemoryCheckpointer<S> _inner = new();

        public int Saves { get; private set; }

        public Task SaveAsync(Checkpoint<S> checkpoint, CancellationToken cancellationToken = default)
        {
            Saves++;
            return _inner.SaveAsync(checkpoint, cancellationToken);
        }

        public Task<Checkpoint<S>?> LoadAsync(string threadId, CancellationToken cancellationToken = default) =>
            _inner.LoadAsync(threadId, cancellationToken);

        public Task<IReadOnlyList<Checkpoint<S>>> GetHistoryAsync(string threadId, CancellationToken cancellationToken = default) =>
            _inner.GetHistoryAsync(threadId, cancellationToken);
    }

    private static StateGraph<S> OneStep() =>
        new StateGraph<S>()
            .AddNode("step", s => s with { N = s.N + 1 })
            .SetEntryPoint("step");

    [Fact]
    public async Task WithoutAThreadId_NoLeaseIsTaken()
    {
        var lease = new ControllableLease();
        CompiledGraph<S> graph = OneStep().Compile(new InMemoryCheckpointer<S>(), lease);

        await graph.RunAsync(new S(0));

        // An unnamed run cannot be resumed or joined, so nothing could collide with it.
        Assert.Equal(0, lease.AcquireAttempts);
    }

    [Fact]
    public async Task ARefusedLease_StopsTheRunBeforeAnyNodeExecutes()
    {
        var executed = false;
        var lease = new ControllableLease { Grant = false };
        CompiledGraph<S> graph = new StateGraph<S>()
            .AddNode("step", s => { executed = true; return s; })
            .SetEntryPoint("step")
            .Compile(new InMemoryCheckpointer<S>(), lease);

        await Assert.ThrowsAsync<GraphExecutionException>(
            () => graph.RunAsync(new S(0), new GraphRunOptions { ThreadId = "t" }));

        Assert.False(executed);
    }

    [Fact]
    public async Task TheLeaseIsReleased_EvenWhenANodeThrows()
    {
        var lease = new ControllableLease();
        CompiledGraph<S> graph = new StateGraph<S>()
            .AddNode("boom", (S _, CancellationToken _) => Task.FromException<S>(new InvalidOperationException("no")))
            .SetEntryPoint("boom")
            .Compile(new InMemoryCheckpointer<S>(), lease);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => graph.RunAsync(new S(0), new GraphRunOptions { ThreadId = "t" }));

        // A lease leaked by a failed run would wedge that thread id until the process restarted.
        Assert.Equal(1, lease.Released);
    }

    [Fact]
    public async Task LosingTheLeaseMidRun_ReportsLossRatherThanCancellation()
    {
        var lease = new ControllableLease();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        CompiledGraph<S> graph = new StateGraph<S>()
            .AddNode("slow", async (s, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return s;
            })
            .AddEdge("slow", StateGraph.End)
            .SetEntryPoint("slow")
            .Compile(new InMemoryCheckpointer<S>(), lease);

        Task<GraphResult<S>> run = graph.RunAsync(new S(0), new GraphRunOptions { ThreadId = "t" });
        await entered.Task;
        lease.Current!.Lose();

        // The caller never cancelled anything: surfacing OperationCanceledException here would
        // send callers hunting for a cancellation that does not exist.
        RunLeaseLostException lost = await Assert.ThrowsAsync<RunLeaseLostException>(() => run);
        Assert.Equal("t", lost.ThreadId);
    }

    [Fact]
    public async Task TheFencingTokenReachesTheCheckpointer()
    {
        var checkpointer = new RecordingCheckpointer();
        CompiledGraph<S> graph = OneStep().Compile(checkpointer, new ControllableLease(token: 7));

        await graph.RunAsync(new S(0), new GraphRunOptions { ThreadId = "t" });

        Assert.Equal([7], checkpointer.Fences);
    }

    [Fact]
    public async Task AnUnfencedLease_DoesNotClaimAnOrdering()
    {
        var checkpointer = new RecordingCheckpointer();
        CompiledGraph<S> graph = OneStep().Compile(checkpointer, new InProcessRunLease());

        await graph.RunAsync(new S(0), new GraphRunOptions { ThreadId = "t" });

        // A per-process counter would be meaningless across instances, so none is invented.
        Assert.Equal([RunLeaseHandle.Unfenced], checkpointer.Fences);
    }

    [Fact]
    public async Task ACheckpointerThatCannotFence_StillRuns()
    {
        var checkpointer = new PlainCheckpointer();
        CompiledGraph<S> graph = OneStep().Compile(checkpointer, new ControllableLease(token: 3));

        GraphResult<S> result = await graph.RunAsync(new S(0), new GraphRunOptions { ThreadId = "t" });

        // The degrade is explicit — the lease still excludes, only the fence is unavailable.
        Assert.Equal(1, result.FinalState.N);
        Assert.Equal(1, checkpointer.Saves);
    }

    [Fact]
    public async Task UpdateState_IsRefusedWhileTheThreadIsRunning()
    {
        var lease = new InProcessRunLease();
        var gate = new TaskCompletionSource();
        CompiledGraph<S> graph = new StateGraph<S>()
            .AddNode("wait", async (s, _) => { await gate.Task; return s; })
            .SetEntryPoint("wait")
            .Compile(new InMemoryCheckpointer<S>(), lease);
        var options = new GraphRunOptions { ThreadId = "t" };

        Task<GraphResult<S>> run = graph.RunAsync(new S(0), options);
        await Task.Delay(50);

        // Editing state under a live run would have the run overwrite the edit at its next
        // checkpoint — the edit would appear to work and then vanish.
        await Assert.ThrowsAsync<GraphExecutionException>(
            () => graph.UpdateStateAsync("t", s => s with { N = 99 }));

        gate.SetResult();
        await run;
    }
}

/// <summary>
/// A checkpointer acting as a fence: what it accepts from a superseded holder, which must be
/// nothing at all.
/// </summary>
public class FencedCheckpointerTests
{
    private sealed record S(int N);

    [Fact]
    public async Task ALowerTokenIsRefused_AfterAHigherOneHasWritten()
    {
        var checkpointer = new InMemoryCheckpointer<S>();

        await checkpointer.SaveFencedAsync(new Checkpoint<S>("t", 1, "a", new S(1)), fencingToken: 5);

        // The stalled holder's write must not land, however long it was gone.
        await Assert.ThrowsAsync<RunLeaseLostException>(
            () => checkpointer.SaveFencedAsync(new Checkpoint<S>("t", 2, "b", new S(2)), fencingToken: 4));

        Checkpoint<S>? latest = await checkpointer.LoadAsync("t");
        Assert.Equal(1, latest!.State.N);
    }

    [Fact]
    public async Task TheSameTokenIsAccepted()
    {
        var checkpointer = new InMemoryCheckpointer<S>();

        await checkpointer.SaveFencedAsync(new Checkpoint<S>("t", 1, "a", new S(1)), fencingToken: 5);
        await checkpointer.SaveFencedAsync(new Checkpoint<S>("t", 2, "b", new S(2)), fencingToken: 5);

        // One holder writes many checkpoints under one token; only a *lower* token is stale.
        Assert.Equal(2, (await checkpointer.GetHistoryAsync("t")).Count);
    }

    [Fact]
    public async Task FencesAreScopedToOneThread()
    {
        var checkpointer = new InMemoryCheckpointer<S>();

        await checkpointer.SaveFencedAsync(new Checkpoint<S>("busy", 1, "a", new S(1)), fencingToken: 99);
        await checkpointer.SaveFencedAsync(new Checkpoint<S>("quiet", 1, "a", new S(1)), fencingToken: 1);

        // Threads are independent workflows; one racing hard must not fence out another.
        Assert.Single(await checkpointer.GetHistoryAsync("quiet"));
    }

    [Fact]
    public async Task UnfencedWritesAreNeverRefused()
    {
        var checkpointer = new InMemoryCheckpointer<S>();

        await checkpointer.SaveFencedAsync(new Checkpoint<S>("t", 1, "a", new S(1)), fencingToken: 9);
        await checkpointer.SaveAsync(new Checkpoint<S>("t", 2, "b", new S(2)));

        // Unfenced means "no ordering claimed", not "token zero" — comparing it would reject
        // every write made by a graph running without a distributed lease.
        Assert.Equal(2, (await checkpointer.GetHistoryAsync("t")).Count);
    }
}

/// <summary>
/// <see cref="SharedStateRunLease"/> over the in-memory store, which really does
/// compare-and-swap — so these exercise the actual algorithm, not a stand-in for it.
/// </summary>
public class SharedStateRunLeaseTests
{
    /// <summary>
    /// Each lease gets its own clock, so a holder can be frozen — the way a crashed or
    /// descheduled instance is — while the store's clock moves on and its lease expires.
    /// </summary>
    private static SharedStateRunLease NewLease(
        InMemorySharedStateStore store, TimeProvider time, TimeSpan? duration = null, string? owner = null) =>
        new(store, duration ?? TimeSpan.FromSeconds(30), owner, time);

    [Fact]
    public async Task OneHolderAtATime()
    {
        var store = new InMemorySharedStateStore(new FakeTimeProvider());
        SharedStateRunLease a = NewLease(store, new FakeTimeProvider(), owner: "a");
        SharedStateRunLease b = NewLease(store, new FakeTimeProvider(), owner: "b");

        await using RunLeaseHandle? first = await a.TryAcquireAsync("t");
        RunLeaseHandle? second = await b.TryAcquireAsync("t");

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task ReleasingLetsTheNextHolderIn()
    {
        var store = new InMemorySharedStateStore(new FakeTimeProvider());
        SharedStateRunLease a = NewLease(store, new FakeTimeProvider(), owner: "a");
        SharedStateRunLease b = NewLease(store, new FakeTimeProvider(), owner: "b");

        RunLeaseHandle? first = await a.TryAcquireAsync("t");
        await first!.DisposeAsync();

        await using RunLeaseHandle? second = await b.TryAcquireAsync("t");
        Assert.NotNull(second);
    }

    [Fact]
    public async Task EachAcquisitionRaisesTheFencingToken()
    {
        var store = new InMemorySharedStateStore(new FakeTimeProvider());
        SharedStateRunLease lease = NewLease(store, new FakeTimeProvider());

        RunLeaseHandle? first = await lease.TryAcquireAsync("t");
        long firstToken = first!.FencingToken;
        await first.DisposeAsync();

        await using RunLeaseHandle? second = await lease.TryAcquireAsync("t");

        // Strictly increasing is the whole contract: it is what lets a checkpointer tell a
        // current holder from a revived one.
        Assert.True(second!.FencingToken > firstToken);
        Assert.NotEqual(RunLeaseHandle.Unfenced, firstToken);
    }

    [Fact]
    public async Task TokensAreCountedPerThread()
    {
        var store = new InMemorySharedStateStore(new FakeTimeProvider());
        SharedStateRunLease lease = NewLease(store, new FakeTimeProvider());

        await using RunLeaseHandle? one = await lease.TryAcquireAsync("one");
        await using RunLeaseHandle? two = await lease.TryAcquireAsync("two");

        // A busy thread must not push another thread's tokens up; each counter stands alone.
        Assert.Equal(one!.FencingToken, two!.FencingToken);
    }

    [Fact]
    public async Task AnExpiredLeaseIsTakenOver_WithAHigherToken()
    {
        var storeTime = new FakeTimeProvider();
        var store = new InMemorySharedStateStore(storeTime);
        var duration = TimeSpan.FromSeconds(30);

        // The crashed instance: its clock never advances, so it never renews — exactly the
        // effect of a process that stopped running without releasing anything.
        SharedStateRunLease dead = NewLease(store, new FakeTimeProvider(), duration, owner: "dead");
        RunLeaseHandle? abandoned = await dead.TryAcquireAsync("t");
        Assert.NotNull(abandoned);

        storeTime.Advance(duration + TimeSpan.FromSeconds(1));

        SharedStateRunLease live = NewLease(store, new FakeTimeProvider(), duration, owner: "live");
        await using RunLeaseHandle? taken = await live.TryAcquireAsync("t");

        // Recovery needs no reaper and no operator — and the new holder outranks the old one,
        // so if the dead instance ever wakes up its checkpoints are refused.
        Assert.NotNull(taken);
        Assert.True(taken.FencingToken > abandoned!.FencingToken);
    }

    [Fact]
    public async Task ConcurrentAcquirers_ElectExactlyOneWinner()
    {
        var store = new InMemorySharedStateStore();
        var lease = new SharedStateRunLease(store, TimeSpan.FromSeconds(30));

        RunLeaseHandle?[] results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(async _ => await lease.TryAcquireAsync("t")));

        RunLeaseHandle[] winners = [.. results.OfType<RunLeaseHandle>()];
        try
        {
            Assert.Single(winners);
        }
        finally
        {
            foreach (RunLeaseHandle winner in winners)
            {
                await winner.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task AStolenLeaseIsReportedLost()
    {
        var store = new InMemorySharedStateStore();
        var lease = new SharedStateRunLease(store, TimeSpan.FromMilliseconds(300));

        await using RunLeaseHandle? handle = await lease.TryAcquireAsync("t");

        // Simulate the store losing the key (eviction, flush, a failover to an empty replica):
        // the holder must find out at its next renewal instead of running on indefinitely.
        await store.RemoveAsync("trellis:graph:lease:t");
        await store.SetAsync("trellis:graph:lease:t", "someone-else", TimeSpan.FromMinutes(1));

        var cancelled = new TaskCompletionSource();
        using CancellationTokenRegistration _ = handle!.Lost.Register(cancelled.SetResult);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(handle.Lost.IsCancellationRequested);
    }

    [Fact]
    public async Task ReleasingDoesNotEvictTheHolderThatDisplacedUs()
    {
        var store = new InMemorySharedStateStore();
        var lease = new SharedStateRunLease(store, TimeSpan.FromSeconds(30));

        RunLeaseHandle? handle = await lease.TryAcquireAsync("t");
        await store.SetAsync("trellis:graph:lease:t", "new-owner", TimeSpan.FromMinutes(1));

        await handle!.DisposeAsync();

        // An unconditional delete here would silently hand the thread to a third instance
        // while the second was still executing it.
        Assert.Equal("new-owner", await store.GetAsync("trellis:graph:lease:t"));
    }

    [Fact]
    public void ANonPositiveDuration_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SharedStateRunLease(new InMemorySharedStateStore(), TimeSpan.Zero));
    }
}
