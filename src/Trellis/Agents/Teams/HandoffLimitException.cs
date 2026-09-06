namespace Trellis.Agents.Teams;

/// <summary>
/// A team run passed the conversation around more times than it was allowed to.
/// </summary>
/// <remarks>
/// Almost always two agents that each believe the other owns the request. The
/// <see cref="Handoffs"/> trail names them, which is usually enough to see which pair of
/// descriptions overlap.
/// </remarks>
public sealed class HandoffLimitException(int limit, IReadOnlyList<HandoffRecord> handoffs)
    : Exception(
        $"The team made {limit} handoffs without anyone answering. " +
        $"Trail: {string.Join(" -> ", handoffs.Select(h => h.From).Append(handoffs[^1].To))}.")
{
    /// <summary>The limit that was hit.</summary>
    public int Limit { get; } = limit;

    /// <summary>Every transfer made, in order.</summary>
    public IReadOnlyList<HandoffRecord> Handoffs { get; } = handoffs;
}
