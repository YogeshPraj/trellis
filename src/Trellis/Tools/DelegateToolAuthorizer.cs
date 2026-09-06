namespace Trellis.Tools;

/// <summary>
/// An authorizer built from a lambda — for approval queues, per-user policy, or any rule
/// simpler to write than to name.
/// </summary>
public sealed class DelegateToolAuthorizer : IToolAuthorizer
{
    private readonly Func<ToolInvocationContext, CancellationToken, ValueTask<ToolAuthorization>> _rule;

    public DelegateToolAuthorizer(
        Func<ToolInvocationContext, CancellationToken, ValueTask<ToolAuthorization>> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _rule = rule;
    }

    /// <summary>Builds one from a synchronous rule.</summary>
    public static DelegateToolAuthorizer FromPredicate(Func<ToolInvocationContext, ToolAuthorization> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new DelegateToolAuthorizer((context, _) => ValueTask.FromResult(rule(context)));
    }

    public ValueTask<ToolAuthorization> AuthorizeAsync(
        ToolInvocationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _rule(context, cancellationToken);
    }
}
