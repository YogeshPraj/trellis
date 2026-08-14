using Microsoft.Extensions.AI;
using System.Text.Json.Serialization;
using System.Text.Json;
using Trellis.Conversations.Archive;
using Trellis.Conversations;
using Trellis.State;

namespace Trellis.Conversations.Storage;

/// <summary>
/// Persists live (hot) conversations so consecutive turns can land on different instances.
/// The archive (<see cref="IConversationArchive"/>) holds cold, compacted history; this
/// holds the working conversation itself — hot messages, rolling summary, context epoch.
/// </summary>
public interface IConversationStore
{
    /// <summary>Loads a conversation, or null when the id is unknown.</summary>
    ValueTask<Conversation?> LoadAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a conversation, rejecting the write with
    /// <see cref="ConversationConcurrencyException"/> when another writer has advanced it
    /// since this copy was loaded. On success the conversation's
    /// <see cref="Conversation.Version"/> moves forward.
    /// </summary>
    ValueTask SaveAsync(Conversation conversation, CancellationToken cancellationToken = default);

    /// <summary>Deletes a conversation. Deleting an unknown id is not an error.</summary>
    ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default);
}
