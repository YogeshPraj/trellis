namespace Trellis.Tools;

/// <summary>
/// Permits only the named tools. The list says what is allowed, never what is forbidden: a
/// deny-list silently admits every tool added after it was written, which is the wrong
/// direction for a security control to fail.
/// </summary>
public sealed class AllowListToolAuthorizer : IToolAuthorizer
{
    private readonly HashSet<string> _allowed;
    private readonly bool _abortOnRefusal;

    /// <param name="allowedToolNames">
    /// Tool names that may run, compared ordinally and case-sensitively — tool names are
    /// identifiers, not prose.
    /// </param>
    /// <param name="abortOnRefusal">
    /// When true a refusal stops the run instead of being reported to the model. Default false:
    /// telling the model it may not call something usually produces a better answer than
    /// cutting the run off mid-thought.
    /// </param>
    public AllowListToolAuthorizer(IEnumerable<string> allowedToolNames, bool abortOnRefusal = false)
    {
        ArgumentNullException.ThrowIfNull(allowedToolNames);
        _allowed = new HashSet<string>(allowedToolNames, StringComparer.Ordinal);
        _abortOnRefusal = abortOnRefusal;
    }

    public ValueTask<ToolAuthorization> AuthorizeAsync(
        ToolInvocationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_allowed.Contains(context.ToolName))
        {
            return ValueTask.FromResult(ToolAuthorization.Allow());
        }

        const string reason = "this tool is not on the allow-list for this agent";
        return ValueTask.FromResult(
            _abortOnRefusal ? ToolAuthorization.Abort(reason) : ToolAuthorization.Deny(reason));
    }
}
