using Microsoft.Extensions.AI;
using Trellis.Conversations.Compaction;
using Trellis.Conversations.Storage;

namespace Trellis.Conversations;

/// <summary>
/// A client-side canonical conversation: the source of truth for multi-turn context,
/// independent of any provider. Agents run against it accumulate history automatically,
/// and its <see cref="Id"/> flows through <see cref="ChatOptions.ConversationId"/> so a
/// conversation-aware router can exploit provider-side context features per endpoint
/// while failover always has the full history to replay.
/// </summary>
/// <remarks>
/// Not thread-safe: run one turn at a time per conversation. In multi-instance
/// deployments this object lives in one process's memory — persist and rehydrate it
/// (or use session affinity) if consecutive turns can land on different instances.
/// </remarks>
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
    /// Input tokens the provider reported for the most recent turn, when it reported any.
    /// This is the exact size of what was sent (instructions + rolling summary + hot history),
    /// so <see cref="ConversationCompactor"/> prefers it over estimation when deciding whether
    /// the token budget is blown. Null with providers that don't report usage.
    /// </summary>
    public long? LastInputTokenCount { get; private set; }

    /// <summary>
    /// Version of this conversation in an <see cref="IConversationStore"/>: 0 until first
    /// saved, then the version the store last wrote. Saving checks it, so a turn built on a
    /// stale copy is rejected instead of silently overwriting another instance's turn.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>Records provider-reported usage for the turn just completed.</summary>
    internal void RecordUsage(UsageDetails? usage)
    {
        if (usage?.InputTokenCount is long input)
        {
            LastInputTokenCount = input;
        }
    }

    /// <summary>Rehydrates a conversation persisted by an <see cref="IConversationStore"/>.</summary>
    internal static Conversation Restore(
        string id,
        IEnumerable<ChatMessage> messages,
        string? summary,
        int contextEpoch,
        int archivedCount,
        long? lastInputTokenCount,
        int version)
    {
        var conversation = new Conversation(id);
        conversation._messages.AddRange(messages);
        conversation.Summary = summary;
        conversation.ContextEpoch = contextEpoch;
        conversation.ArchivedCount = archivedCount;
        conversation.LastInputTokenCount = lastInputTokenCount;
        conversation.Version = version;
        return conversation;
    }

    /// <summary>
    /// Records that a store accepted a write at <paramref name="version"/>. Called by
    /// <see cref="IConversationStore"/> implementations after a successful save; calling it
    /// yourself desynchronizes the optimistic-concurrency check.
    /// </summary>
    public void MarkPersisted(int version) => Version = version;

    /// <summary>Captures this conversation's full state for persistence at <paramref name="version"/>.</summary>
    public ConversationSnapshot ToSnapshot(int version) =>
        new(Id, version, Messages, Summary, ContextEpoch, ArchivedCount, LastInputTokenCount);

    /// <summary>Rebuilds a conversation from persisted state.</summary>
    public static Conversation FromSnapshot(ConversationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Restore(
            snapshot.Id, snapshot.Messages ?? [], snapshot.Summary, snapshot.ContextEpoch,
            snapshot.ArchivedCount, snapshot.LastInputTokenCount, snapshot.Version);
    }

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

    /// <summary>
    /// Compaction kicked off after the previous turn completed (kept off the response path);
    /// the next turn awaits it before building its payload. Await this before persisting
    /// the conversation or shutting down, so an in-flight compaction isn't lost.
    /// </summary>
    public Task? PendingCompaction { get; internal set; }

    internal void ApplyCompaction(int evictCount, string summary)
    {
        _messages.RemoveRange(0, evictCount);
        Summary = summary;
        ContextEpoch++;
        ArchivedCount += evictCount;
    }
}
