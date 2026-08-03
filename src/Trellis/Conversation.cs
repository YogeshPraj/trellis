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

    /// <summary>The hot (verbatim) message history, oldest first. Compacted turns move to the archive.</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>Rolling summary of the cold (compacted) context; null until the first compaction.</summary>
    public string? Summary { get; private set; }

    /// <summary>Bumped on every compaction. Part of <see cref="RoutingId"/> so provider-side deltas invalidate.</summary>
    public int ContextEpoch { get; private set; }

    /// <summary>How many messages have been evicted to cold storage over this conversation's lifetime.</summary>
    public int ArchivedCount { get; private set; }

    /// <summary>
    /// The id sent through <see cref="ChatOptions.ConversationId"/>. Includes the context
    /// epoch after a compaction, because a compacted history must be replayed in full —
    /// a server-side delta against the pre-compaction transcript would be wrong.
    /// </summary>
    public string RoutingId => ContextEpoch == 0 ? Id : $"{Id}:{ContextEpoch}";

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

    internal void ApplyCompaction(int evictCount, string summary)
    {
        _messages.RemoveRange(0, evictCount);
        Summary = summary;
        ContextEpoch++;
        ArchivedCount += evictCount;
    }
}
