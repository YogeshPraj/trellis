namespace Trellis.Tools;

/// <summary>What an <see cref="IToolAuthorizer"/> decided about one tool call.</summary>
public enum ToolAuthorizationDecision
{
    /// <summary>The call proceeds.</summary>
    Allow,

    /// <summary>
    /// The call is refused and the model is told so, as an ordinary tool result. The run
    /// continues, so the agent can explain itself or try a permitted alternative.
    /// </summary>
    Deny,

    /// <summary>
    /// The call is refused and the run stops immediately — no further model round trips. For
    /// violations where letting the model reason about the refusal, and possibly route around
    /// it, is itself unacceptable.
    /// </summary>
    /// <remarks>
    /// Inside an agent run this terminates the tool loop; invoked directly, with no loop to
    /// stop, it throws <see cref="ToolAuthorizationException"/> instead. The reason for the
    /// split is in <c>AuthorizingAIFunction</c>.
    /// </remarks>
    Abort,
}
