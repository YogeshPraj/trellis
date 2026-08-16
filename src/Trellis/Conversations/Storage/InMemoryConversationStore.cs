using Microsoft.Extensions.AI;
using System.Text.Json.Serialization;
using System.Text.Json;
using Trellis.Conversations;
using Trellis.State;

namespace Trellis.Conversations.Storage;

/// <summary>
/// In-process store. Useful for tests and single-instance apps; it still enforces version
/// checks, so code written against it behaves the same on a distributed backend.
/// </summary>
public sealed class InMemoryConversationStore : IReplicatedConversationStore
{
    private readonly Dictionary<string, string> _conversations = [];
    private readonly Lock _lock = new();

    public ValueTask<Conversation?> LoadAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        lock (_lock)
        {
            if (!_conversations.TryGetValue(conversationId, out string? json))
            {
                return ValueTask.FromResult<Conversation?>(null);
            }
            ConversationSnapshot? snapshot = ConversationSerializer.Deserialize(json);
            return ValueTask.FromResult(snapshot is null ? null : ConversationSerializer.ToConversation(snapshot));
        }
    }

    public ValueTask SaveAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        lock (_lock)
        {
            int stored = _conversations.TryGetValue(conversation.Id, out string? json)
                ? ConversationSerializer.Deserialize(json)?.Version ?? 0
                : 0;
            if (stored != conversation.Version)
            {
                throw new ConversationConcurrencyException(conversation.Id, conversation.Version, stored);
            }

            int next = conversation.Version + 1;
            _conversations[conversation.Id] = ConversationSerializer.Serialize(conversation, next);
            conversation.MarkPersisted(next);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<int?> GetVersionAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        lock (_lock)
        {
            return ValueTask.FromResult(_conversations.TryGetValue(conversationId, out string? json)
                ? ConversationSerializer.Deserialize(json)?.Version
                : null);
        }
    }

    public ValueTask ReplaceAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_lock)
        {
            _conversations[snapshot.Id] = ConversationSerializer.SerializeSnapshot(snapshot);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        lock (_lock)
        {
            _conversations.Remove(conversationId);
        }
        return ValueTask.CompletedTask;
    }
}
