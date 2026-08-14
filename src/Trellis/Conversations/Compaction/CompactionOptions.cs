using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text;
using Trellis.Conversations.Archive;
using Trellis.Conversations;
using Trellis.State;
using Trellis.Tokens;

namespace Trellis.Conversations.Compaction;

/// <summary>When and how much hot context to keep.</summary>
/// <remarks>
/// Two independent budgets, whichever trips first: message count (always on) and token
/// count (opt-in via <see cref="MaxHotTokens"/>). Message count is predictable and free;
/// tokens are what context windows and bills are actually denominated in.
/// </remarks>
public sealed class CompactionOptions
{
    /// <summary>Compaction triggers once the hot history exceeds this many messages.</summary>
    public int MaxHotMessages { get; set; } = 40;

    /// <summary>How many recent messages stay hot (verbatim) after a compaction.</summary>
    public int KeepRecentMessages { get; set; } = 12;

    /// <summary>
    /// Optional token budget for the hot context (null = message-count only). The trigger
    /// uses the provider's reported input tokens for the previous turn when available —
    /// exact, and it includes instructions and the rolling summary — and falls back to
    /// <see cref="TokenCounter"/> otherwise.
    /// </summary>
    public int? MaxHotTokens { get; set; }

    /// <summary>
    /// Token budget for the retained tail after a token-triggered compaction. Defaults to a
    /// third of <see cref="MaxHotTokens"/>, leaving headroom so the next few turns don't
    /// immediately re-trip the budget.
    /// </summary>
    public int? KeepRecentTokens { get; set; }

    /// <summary>
    /// Estimates per-message token cost when choosing the eviction boundary.
    /// Defaults to <see cref="HeuristicTokenCounter"/>; swap in a real tokenizer for precision.
    /// </summary>
    public ITokenCounter TokenCounter { get; set; } = HeuristicTokenCounter.Default;

    /// <summary>
    /// Called when a compaction attempt fails (summarizer or archive error). Compaction
    /// failures never fail the user's turn — the turn proceeds uncompacted and compaction
    /// is retried on a later turn. Use this hook for logging/alerting.
    /// </summary>
    public Action<Exception>? OnCompactionFailure { get; set; }
}
