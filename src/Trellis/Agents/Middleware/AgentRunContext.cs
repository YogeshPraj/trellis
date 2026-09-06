using Microsoft.Extensions.AI;

namespace Trellis.Agents.Middleware;

/// <summary>
/// The mutable state of one agent run, as middleware sees it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Messages"/> is this run's payload, already assembled: the agent's system
/// instructions first (when it has any), then the caller's messages, or for a conversation run
/// the rolling summary and hot history. Editing it is how middleware injects retrieved memory,
/// rewrites a system prompt, or redacts before anything is sent.
/// </para>
/// <para>
/// The list is scratch, built fresh per run. Nothing written here reaches a
/// <see cref="Conversations.Conversation"/>, which absorbs only the accepted response — the
/// same rule that keeps self-healing retries out of canonical history. Middleware can inject
/// freely without corrupting what gets persisted.
/// </para>
/// </remarks>
public sealed class AgentRunContext
{
    internal AgentRunContext(List<ChatMessage> messages, ChatOptions? options, Type resultType)
    {
        Messages = messages;
        Options = options;
        ResultType = resultType;
    }

    /// <summary>This run's payload, in the order it will be sent. Mutable.</summary>
    public IList<ChatMessage> Messages { get; }

    /// <summary>
    /// The options for this run — model, tools, temperature. Replaceable, so middleware can
    /// pick a model or narrow a tool set. Clone before mutating anything you did not create;
    /// the instance may be shared with the agent that supplied it.
    /// </summary>
    public ChatOptions? Options { get; set; }

    /// <summary>The type the run must produce. Useful for middleware written against many agents.</summary>
    public Type ResultType { get; }

    /// <summary>
    /// Scratch shared between middleware in one run — a cache key computed on the way in and
    /// read on the way out, a correlation id, a decision one policy wants the next to see.
    /// Not persisted anywhere.
    /// </summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}
