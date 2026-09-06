namespace Trellis.Agents.Teams;

/// <summary>
/// What one agent did with its turn: answered, or handed the conversation to someone else.
/// </summary>
/// <remarks>
/// An agent that hands off produces no <typeparamref name="TResult"/> — it did not answer the
/// question, it declined it. Modelling that as a result with a null value would push the
/// distinction into every caller's null check; making it a separate case means the compiler
/// asks about it once.
/// </remarks>
public readonly record struct AgentTurn<TResult>
{
    private AgentTurn(AgentRunResult<TResult>? result, string? handoffTarget, string? reason)
    {
        Result = result;
        HandoffTarget = handoffTarget;
        Reason = reason;
    }

    /// <summary>The run, when this agent answered. Null on a handoff.</summary>
    public AgentRunResult<TResult>? Result { get; }

    /// <summary>The agent to transfer to, when this agent handed off. Null on an answer.</summary>
    public string? HandoffTarget { get; }

    /// <summary>Why the agent handed off, in its own words. Null on an answer.</summary>
    public string? Reason { get; }

    /// <summary>Whether the conversation moves to another agent.</summary>
    public bool IsHandoff => HandoffTarget is not null;

    /// <summary>The agent answered.</summary>
    public static AgentTurn<TResult> Answered(AgentRunResult<TResult> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(result, null, null);
    }

    /// <summary>The agent transferred the conversation.</summary>
    public static AgentTurn<TResult> HandedOff(string target, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        return new(null, target, reason);
    }
}
