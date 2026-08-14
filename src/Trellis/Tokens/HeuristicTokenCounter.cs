using Microsoft.Extensions.AI;

namespace Trellis.Tokens;

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
