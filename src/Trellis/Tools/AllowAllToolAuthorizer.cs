namespace Trellis.Tools;

/// <summary>
/// Permits every call. The explicit way to say "this agent's tools need no gate" — a decision
/// someone made, rather than one arrived at by leaving an argument null.
/// </summary>
public sealed class AllowAllToolAuthorizer : IToolAuthorizer
{
    /// <summary>A shared instance; the type holds no state.</summary>
    public static AllowAllToolAuthorizer Instance { get; } = new();

    public ValueTask<ToolAuthorization> AuthorizeAsync(
        ToolInvocationContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ToolAuthorization.Allow());
}
