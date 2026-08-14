using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Trellis.Graph.Checkpointing;
using Trellis.Graph.Diagnostics;
using Trellis.Graph.Resilience;

namespace Trellis.Graph;

/// <summary>An executable graph produced by <see cref="StateGraph{TState}.Compile"/>.</summary>
/// <remarks>
/// Concurrent runs on the same thread id would interleave checkpoints and corrupt the
/// workflow, so this graph rejects them within the process. ⚠ The guard is per-process:
/// in a multi-instance deployment, route a given thread id to one instance (or otherwise
/// ensure single-writer semantics) — cross-instance leasing is not provided yet.
/// </remarks>
public sealed class CompiledGraph<TState>
{
    private readonly ConcurrentDictionary<string, byte> _activeThreads = new();
    private readonly IReadOnlyDictionary<string, NodeHandler<TState>> _nodes;
    private readonly IReadOnlyDictionary<string, Func<TState, string>> _routers;
    private readonly IReadOnlyDictionary<string, NodeResilience<TState>> _resilience;
    private readonly string _entryPoint;
    private readonly ICheckpointer<TState>? _checkpointer;

    internal CompiledGraph(
        IReadOnlyDictionary<string, NodeHandler<TState>> nodes,
        IReadOnlyDictionary<string, Func<TState, string>> routers,
        IReadOnlyDictionary<string, NodeResilience<TState>> resilience,
        string entryPoint,
        ICheckpointer<TState>? checkpointer)
    {
        _nodes = nodes;
        _routers = routers;
        _resilience = resilience;
        _entryPoint = entryPoint;
        _checkpointer = checkpointer;
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

        Checkpoint<TState>? checkpoint = await _checkpointer.LoadAsync(threadId, cancellationToken).ConfigureAwait(false)
            ?? throw new GraphExecutionException($"No checkpoint exists for thread '{threadId}'.");

        await _checkpointer
            .SaveAsync(checkpoint with { State = update(checkpoint.State) }, cancellationToken)
            .ConfigureAwait(false);
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
        // A second concurrent run on the same thread id would interleave checkpoints.
        bool guarded = options?.ThreadId is not null;
        if (guarded && !_activeThreads.TryAdd(threadId, 0))
        {
            throw new GraphExecutionException(
                $"Thread '{threadId}' is already running on this graph. Await the active run before starting another.");
        }
        try
        {
            await foreach (GraphEvent<TState> evt in StreamCoreAsync(input, options, threadId, cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            if (guarded)
            {
                _activeThreads.TryRemove(threadId, out _);
            }
        }
    }

    private async IAsyncEnumerable<GraphEvent<TState>> StreamCoreAsync(
        TState input,
        GraphRunOptions? options,
        string threadId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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
            Checkpoint<TState>? checkpoint = await _checkpointer.LoadAsync(threadId, cancellationToken).ConfigureAwait(false);
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
            cancellationToken.ThrowIfCancellationRequested();

            // A resumed run must execute the node it paused in front of instead of pausing again.
            if (!justResumed && interruptBefore?.Contains(node) == true)
            {
                await _checkpointer!
                    .SaveAsync(new Checkpoint<TState>(threadId, step, node, state), cancellationToken)
                    .ConfigureAwait(false);
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
                NodeAttempt outcome = await TryRunAsync(
                    () => handler(state, cancellationToken), node, step, attempt, runContext).ConfigureAwait(false);
                if (outcome.Error is null)
                {
                    state = outcome.State!;
                    break;
                }

                NodeRetryDecision decision = resilience?.Retry is INodeRetryPolicy policy
                    ? await policy
                        .EvaluateAsync(new NodeFailureContext(node, attempt, outcome.Error), cancellationToken)
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
                        await Task.Delay(decision.Delay, cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }

                NodeFallback<TState>? fallback = resilience?.Fallback;
                if (fallback is null)
                {
                    ExceptionDispatchInfo.Throw(outcome.Error);
                }

                NodeAttempt recovery = await TryRunAsync(
                    () => fallback!(state, outcome.Error, cancellationToken),
                    $"{node} (fallback)", step, attempt, runContext).ConfigureAwait(false);
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

            if (_checkpointer is not null)
            {
                await _checkpointer
                    .SaveAsync(new Checkpoint<TState>(threadId, step, next, state), cancellationToken)
                    .ConfigureAwait(false);
            }

            node = next;
        }

        yield return new GraphEvent<TState>(GraphEventType.GraphCompleted, null, step, state);
    }

    /// <summary>One node (or fallback) execution: the new state, or the exception it threw.</summary>
    private readonly record struct NodeAttempt(TState? State, Exception? Error);

    /// <summary>
    /// Runs a node body under its own span and captures failure instead of throwing, so the
    /// caller — an iterator that cannot <c>yield</c> from inside a <c>catch</c> — can emit
    /// retry events. Cancellation is never captured: it means the caller gave up, not that
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
