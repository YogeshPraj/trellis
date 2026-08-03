using System.Runtime.CompilerServices;

namespace Trellis.Graph;

/// <summary>An executable graph produced by <see cref="StateGraph{TState}.Compile"/>.</summary>
public sealed class CompiledGraph<TState>
{
    private readonly IReadOnlyDictionary<string, NodeHandler<TState>> _nodes;
    private readonly IReadOnlyDictionary<string, Func<TState, string>> _routers;
    private readonly string _entryPoint;
    private readonly ICheckpointer<TState>? _checkpointer;

    internal CompiledGraph(
        IReadOnlyDictionary<string, NodeHandler<TState>> nodes,
        IReadOnlyDictionary<string, Func<TState, string>> routers,
        string entryPoint,
        ICheckpointer<TState>? checkpointer)
    {
        _nodes = nodes;
        _routers = routers;
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

            state = await handler(state, cancellationToken).ConfigureAwait(false);
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

    private static string NewThreadId() => Guid.NewGuid().ToString("N");
}
