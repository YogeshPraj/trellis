namespace Trellis.Graph;

/// <summary>An async node handler: receives the current state, returns the next state.</summary>
public delegate Task<TState> NodeHandler<TState>(TState state, CancellationToken cancellationToken);

/// <summary>Well-known graph node names.</summary>
public static class StateGraph
{
    /// <summary>Route to this name to terminate the graph.</summary>
    public const string End = "__end__";
}

/// <summary>
/// A builder for a directed graph of state-transforming nodes.
/// Add nodes and edges, then <see cref="Compile"/> into an executable <see cref="CompiledGraph{TState}"/>.
/// </summary>
public sealed class StateGraph<TState>
{
    private readonly Dictionary<string, NodeHandler<TState>> _nodes = [];
    private readonly Dictionary<string, Func<TState, string>> _routers = [];
    private readonly Dictionary<string, string> _fixedEdges = [];
    private string? _entryPoint;

    /// <summary>Adds an async node.</summary>
    public StateGraph<TState> AddNode(string name, NodeHandler<TState> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (name == StateGraph.End)
        {
            throw new GraphDefinitionException($"'{StateGraph.End}' is reserved and cannot be used as a node name.");
        }
        if (!_nodes.TryAdd(name, handler))
        {
            throw new GraphDefinitionException($"A node named '{name}' has already been added.");
        }
        return this;
    }

    /// <summary>Adds a synchronous node.</summary>
    public StateGraph<TState> AddNode(string name, Func<TState, TState> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return AddNode(name, (state, _) => Task.FromResult(handler(state)));
    }

    /// <summary>Adds an async node that does not need a cancellation token.</summary>
    public StateGraph<TState> AddNode(string name, Func<TState, Task<TState>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return AddNode(name, (state, _) => handler(state));
    }

    /// <summary>
    /// Adds a fan-out/fan-in node: every branch runs concurrently against the same input state,
    /// then <paramref name="merge"/> combines the input state and all branch results into one state.
    /// </summary>
    public StateGraph<TState> AddParallelNode(
        string name,
        IReadOnlyList<NodeHandler<TState>> branches,
        Func<TState, IReadOnlyList<TState>, TState> merge)
    {
        ArgumentNullException.ThrowIfNull(branches);
        ArgumentNullException.ThrowIfNull(merge);
        if (branches.Count == 0)
        {
            throw new GraphDefinitionException($"Parallel node '{name}' needs at least one branch.");
        }

        return AddNode(name, async (state, ct) =>
        {
            TState[] results = await Task.WhenAll(branches.Select(b => b(state, ct))).ConfigureAwait(false);
            return merge(state, results);
        });
    }

    /// <summary>Adds a fan-out/fan-in node whose branches do not need a cancellation token.</summary>
    public StateGraph<TState> AddParallelNode(
        string name,
        IReadOnlyList<Func<TState, Task<TState>>> branches,
        Func<TState, IReadOnlyList<TState>, TState> merge)
    {
        ArgumentNullException.ThrowIfNull(branches);
        return AddParallelNode(
            name,
            [.. branches.Select(b => new NodeHandler<TState>((state, _) => b(state)))],
            merge);
    }

    /// <summary>Adds a fixed edge: after <paramref name="from"/> completes, run <paramref name="to"/>.</summary>
    public StateGraph<TState> AddEdge(string from, string to)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(to);
        ThrowIfEdgeExists(from);
        _fixedEdges[from] = to;
        _routers[from] = _ => to;
        return this;
    }

    /// <summary>
    /// Adds a conditional edge: after <paramref name="from"/> completes, <paramref name="router"/>
    /// inspects the state and returns the name of the next node (or <see cref="StateGraph.End"/>).
    /// </summary>
    public StateGraph<TState> AddConditionalEdge(string from, Func<TState, string> router)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentNullException.ThrowIfNull(router);
        ThrowIfEdgeExists(from);
        _routers[from] = router;
        return this;
    }

    /// <summary>Sets the node the graph starts at.</summary>
    public StateGraph<TState> SetEntryPoint(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _entryPoint = name;
        return this;
    }

    /// <summary>
    /// Validates the graph and produces an executable <see cref="CompiledGraph{TState}"/>.
    /// A node with no outgoing edge implicitly routes to <see cref="StateGraph.End"/>.
    /// </summary>
    public CompiledGraph<TState> Compile(ICheckpointer<TState>? checkpointer = null)
    {
        if (_entryPoint is null)
        {
            throw new GraphDefinitionException("No entry point set. Call SetEntryPoint(...) before Compile().");
        }
        if (!_nodes.ContainsKey(_entryPoint))
        {
            throw new GraphDefinitionException($"Entry point '{_entryPoint}' is not a registered node.");
        }
        foreach ((string from, string to) in _fixedEdges)
        {
            if (!_nodes.ContainsKey(from))
            {
                throw new GraphDefinitionException($"Edge source '{from}' is not a registered node.");
            }
            if (to != StateGraph.End && !_nodes.ContainsKey(to))
            {
                throw new GraphDefinitionException($"Edge '{from}' -> '{to}' targets an unknown node.");
            }
        }
        foreach (string from in _routers.Keys)
        {
            if (!_nodes.ContainsKey(from))
            {
                throw new GraphDefinitionException($"Conditional edge source '{from}' is not a registered node.");
            }
        }

        return new CompiledGraph<TState>(
            new Dictionary<string, NodeHandler<TState>>(_nodes),
            new Dictionary<string, Func<TState, string>>(_routers),
            _entryPoint,
            checkpointer);
    }

    private void ThrowIfEdgeExists(string from)
    {
        if (_routers.ContainsKey(from))
        {
            throw new GraphDefinitionException($"Node '{from}' already has an outgoing edge.");
        }
    }
}
