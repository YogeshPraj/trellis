namespace Trellis.Agents.Middleware;

/// <summary>
/// Middleware from a lambda, for rules shorter to write than to name.
/// </summary>
public sealed class DelegateAgentMiddleware<TResult> : IAgentMiddleware<TResult>
{
    private readonly Func<AgentRunContext, AgentRunDelegate<TResult>, CancellationToken, Task<AgentRunResult<TResult>>> _body;

    public DelegateAgentMiddleware(
        Func<AgentRunContext, AgentRunDelegate<TResult>, CancellationToken, Task<AgentRunResult<TResult>>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        _body = body;
    }

    /// <summary>
    /// Middleware that only prepares the request — retrieval, prompt rules, redaction — and
    /// leaves the result untouched.
    /// </summary>
    public static DelegateAgentMiddleware<TResult> OnRequest(Action<AgentRunContext> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        return new DelegateAgentMiddleware<TResult>((context, next, cancellationToken) =>
        {
            prepare(context);
            return next(context, cancellationToken);
        });
    }

    public Task<AgentRunResult<TResult>> InvokeAsync(
        AgentRunContext context,
        AgentRunDelegate<TResult> next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        return _body(context, next, cancellationToken);
    }
}
