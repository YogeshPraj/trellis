namespace Trellis.Agents.Middleware;

/// <summary>
/// Wraps an agent run, seeing the payload on the way in and the result on the way out.
/// </summary>
/// <remarks>
/// <para>
/// Trellis already composes at the model layer — <c>DelegatingChatClient</c> is how rate
/// limiting, credit metering, and usage recording stack — and at the tool layer, through
/// <see cref="Tools.IToolAuthorizer"/>. This is the third seam: the agent run itself, where the
/// payload and the typed result both exist. Retrieval that injects memory before the call,
/// guardrails that inspect or replace an answer, per-tenant prompt rules, caching, and audit
/// all belong here, and all of them are plugins rather than edits to the runner.
/// </para>
/// <para>
/// The first middleware in the list is the outermost: it sees the request first and the result
/// last, exactly like ASP.NET Core.
/// </para>
/// <para>
/// Implementations may skip <c>next</c> entirely to answer without a model, or
/// invoke it more than once to retry. They share one <see cref="AgentRunContext"/> per run, so
/// a middleware that mutates the payload and then calls <c>next</c> twice must expect the
/// second call to see its own edits.
/// </para>
/// <para>
/// ⚠ Middleware runs on buffered runs only. Streaming has no result to inspect until the last
/// token, and tokens already sent cannot be withdrawn — so an agent that has middleware refuses
/// to stream rather than silently skipping it. Quietly bypassing a guardrail on one code path
/// is worse than not offering the path.
/// </para>
/// </remarks>
/// <typeparam name="TResult">The agent's result type.</typeparam>
public interface IAgentMiddleware<TResult>
{
    /// <summary>Runs this middleware around the rest of the pipeline.</summary>
    Task<AgentRunResult<TResult>> InvokeAsync(
        AgentRunContext context,
        AgentRunDelegate<TResult> next,
        CancellationToken cancellationToken = default);
}
