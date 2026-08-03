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

        await foreach (GraphEvent<TState> evt in StreamAsync(input, options, threadId, cancellationToken).ConfigureAwait(false))
        {
            finalState = evt.State;
            steps = evt.Step;
        }

        return new GraphResult<TState>(finalState, steps, threadId);
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
        TState state = input;
        string node = _entryPoint;
        int step = 0;

        // Resume from the latest checkpoint when the caller supplied a thread id.
        if (_checkpointer is not null && options?.ThreadId is not null)
        {
            Checkpoint<TState>? checkpoint = await _checkpointer.LoadAsync(threadId, cancellationToken).ConfigureAwait(false);
            if (checkpoint is not null)
            {
                state = checkpoint.State;
                node = checkpoint.NextNode;
                step = checkpoint.Step;
            }
        }

        while (node != StateGraph.End)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
