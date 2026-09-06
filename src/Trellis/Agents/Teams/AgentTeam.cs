using Microsoft.Extensions.AI;

namespace Trellis.Agents.Teams;

/// <summary>
/// A set of named agents that can hand a conversation to one another until one answers.
/// </summary>
/// <remarks>
/// <para>
/// Routing here is decided by the model, at run time: each agent is given a
/// <c>transfer_to_*</c> tool for every other member, and calling one moves the conversation.
/// That is deliberately the opposite of <c>StateGraph</c>, where the author decides the route
/// in advance. Both are useful and Trellis already had the second — a fixed pipeline of agents
/// is a graph, and building it as a team instead would be paying a model to rediscover an edge
/// you already knew about. Reach for a team when the routing genuinely depends on what the
/// user said.
/// </para>
/// <para>
/// A team is itself an <see cref="IAgent{TResult}"/>, so a member may be another team. That is
/// all a manager/workers arrangement needs: the manager is a team whose members are teams.
/// </para>
/// <para><b>What the next agent sees.</b> The whole conversation, including the handoff tool
/// call and its result. Hiding the transfer would leave the receiving agent reading a request
/// that mentions a colleague it cannot see any trace of, and would break the tool call/result
/// pairing that providers require.</para>
/// <para><b>Not durable.</b> A team run lives in one process; a crash mid-run loses it. Work
/// that has to survive a restart belongs in a graph with agent nodes and a checkpointer, which
/// is exactly what those are for.</para>
/// <para><b>Tool authorization.</b> Handoff tools are ordinary tools. An
/// <see cref="Tools.IToolAuthorizer"/> on a member sees them, named
/// <c>transfer_to_&lt;name&gt;</c> — useful for forbidding a particular transfer, and a trap
/// if an allow-list forgets to include them, which silently strands that agent.</para>
/// </remarks>
public sealed class AgentTeam<TResult> : IAgent<TResult>
{
    private readonly Dictionary<string, IAgent<TResult>> _members;
    private readonly IAgent<TResult> _entry;
    private readonly AgentTeamOptions _options;

    /// <param name="entryAgent">The agent that takes the first turn.</param>
    /// <param name="members">
    /// Everyone who may be handed to, including <paramref name="entryAgent"/> if it may be
    /// returned to.
    /// </param>
    /// <param name="name">How this team is addressed when nested in another team.</param>
    /// <param name="description">What this team is for, read by a model deciding to hand to it.</param>
    /// <param name="options">Run limits; defaults are used when null.</param>
    public AgentTeam(
        IAgent<TResult> entryAgent,
        IEnumerable<IAgent<TResult>> members,
        string name = "team",
        string description = "A team of agents.",
        AgentTeamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(entryAgent);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _entry = entryAgent;
        _options = options ?? new AgentTeamOptions();
        Name = name;
        Description = description;

        _members = new Dictionary<string, IAgent<TResult>>(StringComparer.Ordinal);
        foreach (IAgent<TResult> member in members.Append(entryAgent))
        {
            // Two members answering to one name means a handoff is a coin flip, and which one
            // ran would depend on enumeration order. Better to refuse to start.
            if (_members.TryGetValue(member.Name, out IAgent<TResult>? existing) && !ReferenceEquals(existing, member))
            {
                throw new ArgumentException(
                    $"Two different agents are both named '{member.Name}'. Names address agents, so they must be unique.",
                    nameof(members));
            }
            _members[member.Name] = member;
        }

        if (_options.MaxHandoffs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.MaxHandoffs, "MaxHandoffs cannot be negative.");
        }
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Description { get; }

    /// <summary>The members, by name.</summary>
    public IReadOnlyDictionary<string, IAgent<TResult>> Members => _members;

    /// <summary>Runs the team on a single user prompt.</summary>
    public Task<AgentTeamResult<TResult>> RunAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return RunAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken);
    }

    /// <summary>Runs the team on a full message history.</summary>
    public async Task<AgentTeamResult<TResult>> RunAsync(
        IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        List<ChatMessage> transcript = [.. messages];
        IAgent<TResult> current = _entry;
        List<HandoffRecord> handoffs = [];

        for (var transfers = 0; ; transfers++)
        {
            IReadOnlyList<AITool> handoffTools = HandoffTools.For(_members.Values, current.Name);

            AgentTurn<TResult> turn = await current
                .TakeTurnAsync(transcript, handoffTools, cancellationToken)
                .ConfigureAwait(false);

            if (!turn.IsHandoff)
            {
                return new AgentTeamResult<TResult>(turn.Result!, current.Name, handoffs);
            }

            if (transfers >= _options.MaxHandoffs)
            {
                handoffs.Add(new HandoffRecord(current.Name, turn.HandoffTarget!, turn.Reason));
                throw new HandoffLimitException(_options.MaxHandoffs, handoffs);
            }

            if (!_members.TryGetValue(turn.HandoffTarget!, out IAgent<TResult>? next))
            {
                // The model named an agent that does not exist. Telling it so and letting it
                // try again beats failing the run: it can usually pick a real colleague, and
                // the alternative is a crash caused by a typo.
                transcript.Add(new ChatMessage(
                    ChatRole.User,
                    $"There is no agent named '{turn.HandoffTarget}'. Available agents: " +
                    $"{string.Join(", ", _members.Keys)}. Answer yourself or transfer to one of those."));
                continue;
            }

            handoffs.Add(new HandoffRecord(current.Name, next.Name, turn.Reason));
            current = next;
        }
    }

    /// <summary>
    /// Takes a turn as a member of an enclosing team: runs to an answer internally, then
    /// reports that answer upward.
    /// </summary>
    /// <remarks>
    /// A nested team does not forward its members' handoff options outward, and should not —
    /// the enclosing team would then be able to address a worker directly and bypass the
    /// manager that owns it. Whether an inner team can hand back out is a routing decision for
    /// whoever built it, expressed by making the outer agents members.
    /// </remarks>
    public async Task<AgentTurn<TResult>> TakeTurnAsync(
        IEnumerable<ChatMessage> messages,
        IReadOnlyList<AITool>? additionalTools = null,
        CancellationToken cancellationToken = default)
    {
        AgentTeamResult<TResult> result = await RunAsync(messages, cancellationToken).ConfigureAwait(false);
        return AgentTurn<TResult>.Answered(result.Run);
    }
}
