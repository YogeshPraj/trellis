using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Trellis.Graph.Checkpointing;
using Trellis.Graph.Diagnostics;
using Trellis.Graph.Leasing;
using Trellis.Graph.Resilience;

namespace Trellis.Graph;

/// <summary>An executable graph produced by <see cref="StateGraph{TState}.Compile"/>.</summary>
/// <remarks>
/// Concurrent runs on the same thread id would interleave checkpoints and fork the workflow,
/// so every run holding a caller-supplied thread id takes an <see cref="IRunLease"/> first.
/// The default lease is exclusive within one process; pass a distributed one to make it
/// exclusive across a fleet.
/// </remarks>
public sealed class CompiledGraph<TState>
{
    private readonly IReadOnlyDictionary<string, NodeHandler<TState>> _nodes;
    private readonly IReadOnlyDictionary<string, Func<TState, string>> _routers;
    private readonly IReadOnlyDictionary<string, NodeResilience<TState>> _resilience;
    private readonly string _entryPoint;
    private readonly ICheckpointer<TState>? _checkpointer;
    private readonly IRunLease _lease;

    internal CompiledGraph(
        IReadOnlyDictionary<string, NodeHandler<TState>> nodes,
        IReadOnlyDictionary<string, Func<TState, string>> routers,
        IReadOnlyDictionary<string, NodeResilience<TState>> resilience,
        string entryPoint,
        ICheckpointer<TState>? checkpointer,
        IRunLease? lease)
    {
        _nodes = nodes;
        _routers = routers;
        _resilience = resilience;
        _entryPoint = entryPoint;
        _checkpointer = checkpointer;
        _lease = lease ?? new InProcessRunLease();
    }

    /// <summary>Runs the graph to completion and returns the final state.</summary>
    public async Task<GraphResult<TState>> RunAsync(
        TState input,
        GraphRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string threadId = options?.ThreadId ?? NewThreadId();
        TState finalState = input;
        int steps = 0;
        GraphEvent<TState>? last = null;

        await foreach (GraphEvent<TState> evt in StreamAsync(input, options, threadId, cancellationToken).ConfigureAwait(false))
        {
            finalState = evt.State;
            steps = evt.Step;
            last = evt;
        }

        return last?.Type == GraphEventType.GraphInterrupted
            ? new GraphResult<TState>(finalState, steps, threadId, GraphRunStatus.Interrupted, last.Node)
            : new GraphResult<TState>(finalState, steps, threadId);
    }

    /// <summary>
    /// Rewrites the latest checkpointed state for a thread — typically to apply human edits
    /// while a run is paused at an interrupt, before resuming.
    /// </summary>
    /// <remarks>
    /// Takes the thread's lease for the duration, so an edit cannot land underneath a run that
    /// is still executing. A paused run holds no lease, which is what makes the edit possible.
    /// </remarks>
    public async Task UpdateStateAsync(
        string threadId,
        Func<TState, TState> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentNullException.ThrowIfNull(update);
        if (_checkpointer is null)
        {
            throw new GraphExecutionException("UpdateStateAsync requires the graph to be compiled with a checkpointer.");
        }

        await using RunLeaseHandle handle =
            await _lease.TryAcquireAsync(threadId, cancellationToken).ConfigureAwait(false)
            ?? throw new GraphExecutionException(
                $"Thread '{threadId}' is currently running, so its state cannot be edited. " +
                "Wait for the run to finish or interrupt it first.");

        Checkpoint<TState>? checkpoint = await _checkpointer.LoadAsync(threadId, cancellationToken).ConfigureAwait(false)
            ?? throw new GraphExecutionException($"No checkpoint exists for thread '{threadId}'.");

        await SaveCheckpointAsync(
            checkpoint with { State = update(checkpoint.State) }, handle, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the graph, yielding an event before and after each node and one final
    /// <see cref="GraphEventType.GraphCompleted"/> event.
    /// </summary>
    public IAsyncEnumerable<GraphEvent<TState>> StreamAsync(
        TState input,
        GraphRunOptions? options = null,
        CancellationToken cancellationToken = default) =>
        StreamAsync(input, options, options?.ThreadId ?? NewThreadId(), cancellationToken);

    private async IAsyncEnumerable<GraphEvent<TState>> StreamAsync(
        TState input,
        GraphRunOptions? options,
        string threadId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A thread id the caller chose can be resumed, so a second run on it would interleave
        // checkpoints. A generated one is private to this run and needs no lease.
        bool guarded = options?.ThreadId is not null;
        RunLeaseHandle? handle = guarded
            ? await _lease.TryAcquireAsync(threadId, cancellationToken).ConfigureAwait(false)
                ?? throw new GraphExecutionException(
                    $"Thread '{threadId}' is already running. Await the active run before starting another.")
            : null;

        try
        {
            // The run stops promptly when the lease goes away rather than executing further
            // nodes it no longer owns; the fencing token is what guarantees any write already
            // in flight is refused.
            using CancellationTokenSource? linked = handle is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handle.Lost);

            await foreach (GraphEvent<TState> evt in StreamCoreAsync(
                input, options, threadId, handle, linked?.Token ?? cancellationToken, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            if (handle is not null)
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // runToken is cancelled by the caller OR by losing the lease; callerToken only by the
    // caller. Keeping both is what lets a lost lease be reported as such instead of as a
    // cancellation the caller never asked for.
    private async IAsyncEnumerable<GraphEvent<TState>> StreamCoreAsync(
        TState input,
        GraphRunOptions? options,
        string threadId,
        RunLeaseHandle? handle,
        [EnumeratorCancellation] CancellationToken runToken,
        CancellationToken callerToken)
    {
        // Stopped by 'using' however the enumeration ends — completion, interrupt, or an
        // exception thrown out of a node. Node spans take its context as an explicit parent
        // rather than relying on Activity.Current, which a slow consumer could displace
        // between yields.
        using Activity? runActivity = GraphTelemetry.StartRun(threadId);
        ActivityContext runContext = runActivity?.Context ?? default;

        int maxSteps = options?.MaxSteps ?? GraphRunOptions.DefaultMaxSteps;
        IReadOnlyCollection<string>? interruptBefore = options?.InterruptBefore;
        TState state = input;
        string node = _entryPoint;
        int step = 0;
        bool justResumed = false;

        if (interruptBefore is { Count: > 0 })
        {
            if (_checkpointer is null)
            {
                throw new GraphExecutionException("InterruptBefore requires the graph to be compiled with a checkpointer.");
            }
            if (options?.ThreadId is null)
            {
                throw new GraphExecutionException("InterruptBefore requires GraphRunOptions.ThreadId so the run can be resumed.");
            }
        }

        // Resume from the latest checkpoint when the caller supplied a thread id.
        if (_checkpointer is not null && options?.ThreadId is not null)
        {
            Checkpoint<TState>? checkpoint = await _checkpointer.LoadAsync(threadId, runToken).ConfigureAwait(false);
            if (checkpoint is not null)
            {
                state = checkpoint.State;
                node = checkpoint.NextNode;
                step = checkpoint.Step;
                justResumed = true;
            }
        }

        while (node != StateGraph.End)
        {
            ThrowIfLeaseLost(handle);
            callerToken.ThrowIfCancellationRequested();

            // A resumed run must execute the node it paused in front of instead of pausing again.
            if (!justResumed && interruptBefore?.Contains(node) == true)
            {
                await SaveCheckpointAsync(
                    new Checkpoint<TState>(threadId, step, node, state), handle, runToken).ConfigureAwait(false);
                yield return new GraphEvent<TState>(GraphEventType.GraphInterrupted, node, step, state);
                yield break;
            }
            justResumed = false;

            if (step >= maxSteps)
            {
                throw new GraphRecursionException(
                    $"Graph exceeded {maxSteps} steps without reaching '{StateGraph.End}'. " +
                    "Raise GraphRunOptions.MaxSteps if this is intentional.");
            }

            if (!_nodes.TryGetValue(node, out NodeHandler<TState>? handler))
            {
                throw new GraphExecutionException($"Routed to unknown node '{node}'.");
            }

            yield return new GraphEvent<TState>(GraphEventType.NodeStarted, node, step, state);

            _resilience.TryGetValue(node, out NodeResilience<TState>? resilience);
            int attempt = 1;
            while (true)
            {
                NodeAttempt outcome = await RunNodeAsync(
                    () => handler(state, runToken), node, step, attempt, runContext, handle).ConfigureAwait(false);
                if (outcome.Error is null)
                {
                    state = outcome.State!;
                    break;
                }

                NodeRetryDecision decision = resilience?.Retry is INodeRetryPolicy policy
                    ? await policy
                        .EvaluateAsync(new NodeFailureContext(node, attempt, outcome.Error), runToken)
                        .ConfigureAwait(false)
                    : NodeRetryDecision.Stop;

                if (decision.ShouldRetry)
                {
                    GraphTelemetry.RecordRetry(node);
                    yield return new GraphEvent<TState>(
                        GraphEventType.NodeRetrying, node, step, state, Attempt: attempt, Error: outcome.Error);
                    attempt++;
                    if (decision.Delay > TimeSpan.Zero)
                    {
                        await DelayAsync(decision.Delay, runToken, handle).ConfigureAwait(false);
                    }
                    continue;
                }

                NodeFallback<TState>? fallback = resilience?.Fallback;
                if (fallback is null)
                {
                    ExceptionDispatchInfo.Throw(outcome.Error);
                }

                NodeAttempt recovery = await RunNodeAsync(
                    () => fallback!(state, outcome.Error, runToken),
                    $"{node} (fallback)", step, attempt, runContext, handle).ConfigureAwait(false);
                GraphTelemetry.RecordFallback(node, recovery.Error is null);
                if (recovery.Error is not null)
                {
                    // Surface both failures: the fallback's error alone hides why it ran.
                    throw new GraphExecutionException(
                        $"Node '{node}' failed after {attempt} attempt(s) and its fallback also failed.",
                        new AggregateException(outcome.Error, recovery.Error));
                }

                yield return new GraphEvent<TState>(
                    GraphEventType.NodeFallbackApplied, node, step, recovery.State!, Attempt: attempt, Error: outcome.Error);
                state = recovery.State!;
                break;
            }
            step++;

            string next = _routers.TryGetValue(node, out Func<TState, string>? router)
                ? router(state)
                : StateGraph.End;
            if (next != StateGraph.End && !_nodes.ContainsKey(next))
            {
                throw new GraphExecutionException(
                    $"Router of node '{node}' returned unknown node '{next}'.");
            }

            yield return new GraphEvent<TState>(GraphEventType.NodeCompleted, node, step, state, next);

            await SaveCheckpointAsync(
                new Checkpoint<TState>(threadId, step, next, state), handle, runToken).ConfigureAwait(false);

            node = next;
        }

        yield return new GraphEvent<TState>(GraphEventType.GraphCompleted, null, step, state);
    }

    /// <summary>
    /// Writes a checkpoint through the fence when the checkpointer supports one, and plainly
    /// when it does not — the explicit degrade an <see cref="IFencedCheckpointer{TState}"/>
    /// exists to make visible.
    /// </summary>
    private async Task SaveCheckpointAsync(
        Checkpoint<TState> checkpoint, RunLeaseHandle? handle, CancellationToken cancellationToken)
    {
        if (_checkpointer is null)
        {
            return;
        }

        if (_checkpointer is IFencedCheckpointer<TState> fenced)
        {
            await fenced
                .SaveFencedAsync(checkpoint, handle?.FencingToken ?? RunLeaseHandle.Unfenced, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await _checkpointer.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
    }

    private static void ThrowIfLeaseLost(RunLeaseHandle? handle)
    {
        if (handle is not null && handle.Lost.IsCancellationRequested)
        {
            throw new RunLeaseLostException(handle.ThreadId, handle.FencingToken);
        }
    }

    /// <summary>One node (or fallback) execution: the new state, or the exception it threw.</summary>
    private readonly record struct NodeAttempt(TState? State, Exception? Error);

    /// <summary>
    /// Runs a node and translates a cancellation caused by lease loss into
    /// <see cref="RunLeaseLostException"/>, so losing the thread never looks like the caller
    /// having cancelled.
    /// </summary>
    private static async Task<NodeAttempt> RunNodeAsync(
        Func<Task<TState>> body,
        string node,
        int step,
        int attempt,
        ActivityContext parent,
        RunLeaseHandle? handle)
    {
        try
        {
            return await TryRunAsync(body, node, step, attempt, parent).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            ThrowIfLeaseLostOnCancel(handle, ex);
            throw;
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken runToken, RunLeaseHandle? handle)
    {
        try
        {
            await Task.Delay(delay, runToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            ThrowIfLeaseLostOnCancel(handle, ex);
            throw;
        }
    }

    private static void ThrowIfLeaseLostOnCancel(RunLeaseHandle? handle, OperationCanceledException cause)
    {
        if (handle is not null && handle.Lost.IsCancellationRequested)
        {
            throw new RunLeaseLostException(handle.ThreadId, handle.FencingToken, cause);
        }
    }

    /// <summary>
    /// Runs a node body under its own span and captures failure instead of throwing, so the
    /// caller — an iterator that cannot <c>yield</c> from inside a <c>catch</c> — can emit
    /// retry events. Cancellation is never captured: it means the run is over, not that
    /// the node failed.
    /// </summary>
    private static async Task<NodeAttempt> TryRunAsync(
        Func<Task<TState>> body,
        string node,
        int step,
        int attempt,
        ActivityContext parent)
    {
        using Activity? activity = GraphTelemetry.StartNode(node, step, attempt, parent);
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            var result = new NodeAttempt(await body().ConfigureAwait(false), null);
            GraphTelemetry.RecordNode(activity, node, Stopwatch.GetElapsedTime(startedAt), null);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            GraphTelemetry.RecordNode(activity, node, Stopwatch.GetElapsedTime(startedAt), ex);
            return new(default, ex);
        }
    }

    private static string NewThreadId() => Guid.NewGuid().ToString("N");
}
