using Microsoft.Extensions.AI;

namespace Trellis.Evals;

/// <summary>One thing to ask an agent, and what a good answer looks like.</summary>
/// <typeparam name="TResult">The agent's result type.</typeparam>
public sealed record EvalCase<TResult>
{
    /// <param name="id">
    /// Stable across runs — it is what a baseline is keyed on. Renaming a case makes it a new
    /// case with no history, which is worth doing deliberately rather than by accident.
    /// </param>
    /// <param name="input">The user prompt.</param>
    public EvalCase(string id, string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(input);
        Id = id;
        Messages = [new ChatMessage(ChatRole.User, input)];
    }

    /// <param name="id">Stable identifier, keyed on by baselines.</param>
    /// <param name="messages">A full conversation, for cases about behaviour mid-thread.</param>
    public EvalCase(string id, IEnumerable<ChatMessage> messages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(messages);
        Id = id;
        Messages = [.. messages];
    }

    /// <summary>Stable identifier.</summary>
    public string Id { get; }

    /// <summary>What the agent is asked.</summary>
    public IReadOnlyList<ChatMessage> Messages { get; }

    /// <summary>
    /// The expected answer, for scorers that compare against one. Left unset for open-ended
    /// cases graded by a rubric instead.
    /// </summary>
    public TResult? Expected { get; init; }

    /// <summary>
    /// What a good answer must do, in words. Read by a model-graded scorer, and useful to a
    /// human reading a failure.
    /// </summary>
    public string? Rubric { get; init; }

    /// <summary>Free-form labels — capability, owning team, ticket. Carried into the report.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
