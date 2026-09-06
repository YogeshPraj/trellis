namespace Trellis.Tools;

/// <summary>One authorizer's verdict on one tool call.</summary>
/// <remarks>
/// <para>
/// <see cref="Deny"/> is the right default for policy, <see cref="Abort"/> for security. Both
/// guarantee the tool body does not execute; they differ in what happens next. A denied call is
/// reported to the model as a tool result, so the agent can say "I am not allowed to do that"
/// or reach for a permitted alternative — the conversation continues. An aborted call stops the
/// run where it stands, with no further model round trips, so the model never gets to respond
/// to the refusal at all.
/// </para>
/// <para>
/// A denial's <see cref="Reason"/> is shown to the model, so write it as an instruction rather
/// than an error code, and keep secrets out of it.
/// </para>
/// </remarks>
public readonly record struct ToolAuthorization
{
    private ToolAuthorization(ToolAuthorizationDecision decision, string? reason)
    {
        Decision = decision;
        Reason = reason;
    }

    /// <summary>What was decided.</summary>
    public ToolAuthorizationDecision Decision { get; }

    /// <summary>Why, for a refusal. Null when allowed.</summary>
    public string? Reason { get; }

    /// <summary>Whether the call may proceed.</summary>
    public bool IsAllowed => Decision == ToolAuthorizationDecision.Allow;

    /// <summary>Lets the call through.</summary>
    public static ToolAuthorization Allow() => new(ToolAuthorizationDecision.Allow, null);

    /// <summary>Refuses the call and tells the model why, letting the run continue.</summary>
    public static ToolAuthorization Deny(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(ToolAuthorizationDecision.Deny, reason);
    }

    /// <summary>Refuses the call and ends the run with <see cref="ToolAuthorizationException"/>.</summary>
    public static ToolAuthorization Abort(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(ToolAuthorizationDecision.Abort, reason);
    }
}
