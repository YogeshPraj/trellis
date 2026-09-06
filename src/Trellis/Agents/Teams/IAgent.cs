using Microsoft.Extensions.AI;

namespace Trellis.Agents.Teams;

/// <summary>
/// Something that can take a turn in a conversation and either answer or hand off.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets agents be held in a collection and addressed by name. Both
/// <see cref="Agent{TResult}"/> and <c>AgentTeam&lt;TResult&gt;</c> implement it, which is
/// what makes a team usable as a member of another team — a manager whose workers are
/// themselves teams needs no separate concept.
/// </para>
/// <para>
/// A team is homogeneous in <typeparamref name="TResult"/>. Members that answered with
/// different types could not be composed into one answer, and the caller would be left
/// switching on a type it cannot name.
/// </para>
/// </remarks>
public interface IAgent<TResult>
{
    /// <summary>
    /// How this agent is addressed. Handoff tools are named from it, and the model reads it,
    /// so it should look like an identifier: <c>billing</c>, not <c>Billing Department</c>.
    /// </summary>
    string Name { get; }

    /// <summary>What this agent is for. The model reads this to decide whether to hand off to it.</summary>
    string Description { get; }

    /// <summary>
    /// Takes one turn.
    /// </summary>
    /// <param name="messages">The conversation so far, including every earlier agent's turn.</param>
    /// <param name="additionalTools">
    /// Tools supplied for this turn only — the handoff tools, when running in a team. Merged
    /// with whatever the agent was built with; nothing is replaced.
    /// </param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    Task<AgentTurn<TResult>> TakeTurnAsync(
        IEnumerable<ChatMessage> messages,
        IReadOnlyList<AITool>? additionalTools = null,
        CancellationToken cancellationToken = default);
}
