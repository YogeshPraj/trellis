using Trellis.Graph;

namespace Trellis;

/// <summary>Runs agents as graph nodes, bridging Trellis and Trellis.Graph.</summary>
public static class StateGraphAgentExtensions
{
    /// <summary>
    /// Adds a node that runs <paramref name="agent"/>: <paramref name="prompt"/> builds the
    /// prompt from the state, and <paramref name="apply"/> folds the typed result back in.
    /// </summary>
    public static StateGraph<TState> AddAgentNode<TState, TResult>(
        this StateGraph<TState> graph,
        string name,
        Agent<TResult> agent,
        Func<TState, string> prompt,
        Func<TState, TResult, TState> apply)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(apply);

        return graph.AddNode(name, async (state, ct) =>
        {
            AgentRunResult<TResult> result = await agent.RunAsync(prompt(state), ct).ConfigureAwait(false);
            return apply(state, result.Output);
        });
    }

    /// <summary>
    /// Adds a node that runs a dependency-injected agent; <paramref name="deps"/> selects
    /// this run's dependencies from the state.
    /// </summary>
    public static StateGraph<TState> AddAgentNode<TState, TDeps, TResult>(
        this StateGraph<TState> graph,
        string name,
        Agent<TDeps, TResult> agent,
        Func<TState, TDeps> deps,
        Func<TState, string> prompt,
        Func<TState, TResult, TState> apply)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(apply);

        return graph.AddNode(name, async (state, ct) =>
        {
            AgentRunResult<TResult> result = await agent
                .RunAsync(deps(state), prompt(state), ct)
                .ConfigureAwait(false);
            return apply(state, result.Output);
        });
    }
}
