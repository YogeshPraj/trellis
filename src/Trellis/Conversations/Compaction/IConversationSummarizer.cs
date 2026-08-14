using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text;
using Trellis.Conversations.Archive;
using Trellis.Conversations;
using Trellis.State;
using Trellis.Tokens;

namespace Trellis.Conversations.Compaction;

/// <summary>
/// Compresses evicted (cold) turns into an updated rolling summary (Strategy).
/// The summary travels with every request; the verbatim turns go to the archive.
/// </summary>
public interface IConversationSummarizer
{
    Task<string> SummarizeAsync(
        string? existingSummary,
        IReadOnlyList<ChatMessage> evictedMessages,
        CancellationToken cancellationToken = default);
}
