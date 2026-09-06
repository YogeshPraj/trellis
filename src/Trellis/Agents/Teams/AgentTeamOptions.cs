namespace Trellis.Agents.Teams;

/// <summary>How a team runs.</summary>
public sealed class AgentTeamOptions
{
    /// <summary>
    /// How many transfers one run may make before the team gives up. Default 8.
    /// </summary>
    /// <remarks>
    /// Two agents that each believe the other owns a request will pass it back and forth
    /// forever, paying for a model call every time. This is the multi-agent version of the
    /// graph's step limit, and it exists for the same reason: a loop that costs money must
    /// have a ceiling that was chosen rather than discovered.
    /// </remarks>
    public int MaxHandoffs { get; set; } = 8;
}
