namespace Trellis.Agents.Teams;

/// <summary>The outcome of a team run: who answered, what they said, and how it got there.</summary>
/// <remarks>
/// The handoff trail is part of the result rather than something to reconstruct from logs.
/// "Which agent actually answered this, and why did it end up there" is the first question
/// anyone asks of a multi-agent system, and it should not require a tracing backend.
/// </remarks>
public sealed class AgentTeamResult<TResult>(
    AgentRunResult<TResult> run, string answeredBy, IReadOnlyList<HandoffRecord> handoffs)
{
    /// <summary>The answering agent's run.</summary>
    public AgentRunResult<TResult> Run { get; } = run;

    /// <summary>The typed answer.</summary>
    public TResult Output => Run.Output;

    /// <summary>The name of the agent that answered.</summary>
    public string AnsweredBy { get; } = answeredBy;

    /// <summary>Every transfer, in order. Empty when the entry agent answered directly.</summary>
    public IReadOnlyList<HandoffRecord> Handoffs { get; } = handoffs;
}
