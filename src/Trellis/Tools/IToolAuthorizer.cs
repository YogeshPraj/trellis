namespace Trellis.Tools;

/// <summary>Decides whether a tool call the model requested is allowed to run.</summary>
/// <remarks>
/// <para>
/// Without one of these, every tool an agent holds is unconditionally callable by whatever the
/// model decides — including a model steered by text it read from a web page, a document, or
/// another tool's result. This is the gate, and it sits on the path of every call.
/// </para>
/// <para>
/// Authorizers run before the tool does, must not have side effects of their own, and should
/// be fast: they are on the hot path. One that needs to ask a human belongs behind a queue and
/// a cached decision, not an inline prompt.
/// </para>
/// <para>
/// An authorizer that throws refuses the call rather than letting it through. A policy engine
/// being down is not evidence of permission.
/// </para>
/// </remarks>
public interface IToolAuthorizer
{
    /// <summary>Rules on one tool call.</summary>
    ValueTask<ToolAuthorization> AuthorizeAsync(
        ToolInvocationContext context, CancellationToken cancellationToken = default);
}
