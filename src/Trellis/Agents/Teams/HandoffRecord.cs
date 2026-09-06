namespace Trellis.Agents.Teams;

/// <summary>One transfer that happened during a team run.</summary>
/// <param name="From">The agent that gave the conversation up.</param>
/// <param name="To">The agent that took it.</param>
/// <param name="Reason">Why, in the handing agent's own words. Null when it gave none.</param>
public sealed record HandoffRecord(string From, string To, string? Reason);
