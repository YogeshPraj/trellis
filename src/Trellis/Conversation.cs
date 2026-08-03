using Microsoft.Extensions.AI;

namespace Trellis;

/// <summary>
/// A client-side canonical conversation: the source of truth for multi-turn context,
/// independent of any provider. Agents run against it accumulate history automatically,
/// and its <see cref="Id"/> flows through <see cref="ChatOptions.ConversationId"/> so a
/// conversation-aware router can exploit provider-side context features per endpoint
/// while failover always has the full history to replay.
/// </summary>
public sealed class Conversation
{
    private readonly List<ChatMessage> _messages = [];

    public Conversation(string? id = null)
    {
        Id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id;
    }

    /// <summary>Logical conversation id — yours, never a provider's.</summary>
    public string Id { get; }

    /// <summary>The full message history, oldest first.</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    public void Add(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messages.Add(message);
    }

    public void AddRange(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages.AddRange(messages);
    }
}
