using Microsoft.Extensions.AI;

namespace Trellis;

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

/// <summary>
/// Dependency-free default: approximates tokens from text length. Deliberately rough — it
/// exists so token budgets work out of the box without pulling a tokenizer (and its model
/// vocabularies) into every deployment.
/// </summary>
/// <param name="charactersPerToken">
/// Average characters per token (default 4, the usual English/BPE rule of thumb).
/// </param>
/// <param name="perMessageOverhead">
/// Tokens added per message for role and delimiter framing (default 4, matching the
/// commonly documented chat-format overhead).
/// </param>
public sealed class HeuristicTokenCounter(double charactersPerToken = 4.0, int perMessageOverhead = 4) : ITokenCounter
{
    private readonly double _charactersPerToken = charactersPerToken > 0
        ? charactersPerToken
        : throw new ArgumentOutOfRangeException(nameof(charactersPerToken));

    /// <summary>A shared instance with the default ratios.</summary>
    public static HeuristicTokenCounter Default { get; } = new();

    public int CountTokens(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        int characters = message.Text.Length;
        foreach (AIContent content in message.Contents)
        {
            // Tool traffic is JSON the provider still pays for, and it never lands in Text.
            characters += content switch
            {
                FunctionCallContent call => call.Name.Length + EstimateArgumentLength(call),
                FunctionResultContent result => result.Result?.ToString()?.Length ?? 0,
                _ => 0,
            };
        }

        return perMessageOverhead + (int)Math.Ceiling(characters / _charactersPerToken);
    }

    private static int EstimateArgumentLength(FunctionCallContent call)
    {
        if (call.Arguments is null)
        {
            return 0;
        }
        int length = 0;
        foreach (KeyValuePair<string, object?> argument in call.Arguments)
        {
            length += argument.Key.Length + (argument.Value?.ToString()?.Length ?? 0) + 4;
        }
        return length;
    }
}
