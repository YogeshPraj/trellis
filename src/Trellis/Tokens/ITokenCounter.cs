using Microsoft.Extensions.AI;
using Trellis.Conversations.Compaction;

namespace Trellis.Tokens;

/// <summary>
/// Estimates how many prompt tokens a message will cost (Strategy). Used to decide when a
/// conversation's hot context is over budget and where to cut it.
/// </summary>
/// <remarks>
/// Exact counts are provider- and model-specific. When the provider reports real usage,
/// <see cref="ConversationCompactor"/> prefers that for the *trigger*; a counter is still
/// needed to pick the eviction *boundary*, because usage is reported for the payload as a
/// whole and never per message. Plug in a real tokenizer (e.g. Microsoft.ML.Tokenizers)
/// when precision matters.
/// </remarks>
public interface ITokenCounter
{
    /// <summary>Estimated prompt tokens for one message, including per-message framing overhead.</summary>
    int CountTokens(ChatMessage message);

    /// <summary>Estimated prompt tokens for a sequence of messages.</summary>
    int CountTokens(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        int total = 0;
        foreach (ChatMessage message in messages)
        {
            total += CountTokens(message);
        }
        return total;
    }
}
