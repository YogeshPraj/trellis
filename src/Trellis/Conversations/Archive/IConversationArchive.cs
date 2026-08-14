using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text;
using Trellis.Conversations;
using Trellis.State;

namespace Trellis.Conversations.Archive;

/// <summary>
/// Cold storage for evicted turns (Repository). Nothing is lost by compaction — the full
/// verbatim history stays retrievable for display, audit, or search.
/// </summary>
public interface IConversationArchive
{
    ValueTask ArchiveAsync(
        string conversationId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every archived (cold) message for a conversation, oldest first.</summary>
    ValueTask<IReadOnlyList<ChatMessage>> LoadAsync(
        string conversationId,
        CancellationToken cancellationToken = default);
}
