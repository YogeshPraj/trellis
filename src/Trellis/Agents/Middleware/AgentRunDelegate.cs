namespace Trellis.Agents.Middleware;

/// <summary>
/// The rest of the pipeline: the next middleware, or the model call itself at the end.
/// </summary>
/// <remarks>
/// Calling it runs the remainder of the agent run and hands back its result. Middleware may
/// call it once, more than once (a retry that re-reads the context), or not at all (a cache
/// hit, or a refusal that answers without reaching a model).
/// </remarks>
public delegate Task<AgentRunResult<TResult>> AgentRunDelegate<TResult>(
    AgentRunContext context, CancellationToken cancellationToken);
