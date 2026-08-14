using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text;
using Trellis.Conversations.Archive;
using Trellis.Conversations;
using Trellis.State;
using Trellis.Tokens;

namespace Trellis.Conversations.Compaction;

/// <summary>
/// Keeps a conversation's hot context bounded: when the verbatim history exceeds
/// <see cref="CompactionOptions.MaxHotMessages"/>, the oldest turns are folded into the
/// rolling summary (cold, in-prompt) and archived verbatim (cold, in-store), leaving the
/// most recent <see cref="CompactionOptions.KeepRecentMessages"/> hot. Each compaction bumps
/// the conversation's <see cref="Conversation.ContextEpoch"/>, which changes its routing id
/// so conversation-aware routers replay full history instead of an invalid delta.
/// </summary>
/// <remarks>
/// The eviction boundary is adjusted so a tool call/result chain is never split: if the
/// first message that would stay hot is a tool result, the boundary advances past the whole
/// chain (providers reject histories that start with an orphaned tool result). Compaction
/// failures are swallowed by design — see <see cref="CompactionOptions.OnCompactionFailure"/>.
/// </remarks>
public sealed class ConversationCompactor
{
    private readonly IConversationSummarizer _summarizer;
    private readonly IConversationArchive? _archive;
    private readonly CompactionOptions _options;

    public ConversationCompactor(
        IConversationSummarizer summarizer,
        IConversationArchive? archive = null,
        CompactionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(summarizer);
        _summarizer = summarizer;
        _archive = archive;
        _options = options ?? new CompactionOptions();
        if (_options.KeepRecentMessages < 1 || _options.KeepRecentMessages >= _options.MaxHotMessages)
        {
            throw new ArgumentException(
                "KeepRecentMessages must be at least 1 and smaller than MaxHotMessages.", nameof(options));
        }
        if (_options.MaxHotTokens is int maxTokens)
        {
            if (maxTokens < 1)
            {
                throw new ArgumentException("MaxHotTokens must be positive.", nameof(options));
            }
            if (_options.KeepRecentTokens is int keepTokens && (keepTokens < 1 || keepTokens >= maxTokens))
            {
                throw new ArgumentException(
                    "KeepRecentTokens must be at least 1 and smaller than MaxHotTokens.", nameof(options));
            }
        }
        else if (_options.KeepRecentTokens is not null)
        {
            throw new ArgumentException(
                "KeepRecentTokens has no effect without MaxHotTokens.", nameof(options));
        }
        ArgumentNullException.ThrowIfNull(_options.TokenCounter);
    }

    /// <summary>
    /// Compacts when the hot history is over budget. Returns true when a compaction ran.
    /// Never throws for summarizer/archive failures — the conversation is left untouched
    /// and <see cref="CompactionOptions.OnCompactionFailure"/> is invoked instead, so an
    /// internal optimization failure can never fail a user's turn.
    /// </summary>
    public async Task<bool> CompactIfNeededAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        bool overMessages = conversation.Messages.Count > _options.MaxHotMessages;
        (bool overTokens, int keepTokenBudget) = EvaluateTokenBudget(conversation);
        if (!overMessages && !overTokens)
        {
            return false;
        }

        // Each budget proposes a boundary; the stricter one (evicting more) wins.
        int boundary = overMessages ? conversation.Messages.Count - _options.KeepRecentMessages : 0;
        if (overTokens)
        {
            boundary = Math.Max(boundary, TokenBoundary(conversation.Messages, keepTokenBudget));
        }

        int evictCount = AdjustBoundaryPastToolChains(conversation.Messages, boundary);
        if (evictCount <= 0 || evictCount >= conversation.Messages.Count)
        {
            return false;
        }
        List<ChatMessage> evicted = [.. conversation.Messages.Take(evictCount)];

        string summary;
        try
        {
            summary = await _summarizer
                .SummarizeAsync(conversation.Summary, evicted, cancellationToken)
                .ConfigureAwait(false);
            if (_archive is not null)
            {
                await _archive.ArchiveAsync(conversation.Id, evicted, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _options.OnCompactionFailure?.Invoke(ex);
            return false;
        }

        conversation.ApplyCompaction(evictCount, summary);
        return true;
    }

    /// <summary>
    /// Whether the hot context is over its token budget, and how many tokens the retained
    /// tail may then use. The trigger prefers the provider's reported input tokens (exact,
    /// and it covers instructions and the rolling summary too) over estimation.
    /// </summary>
    /// <remarks>
    /// Reported usage always exceeds what the counter can attribute to the hot messages —
    /// instructions, the rolling summary, images and provider framing are real prompt tokens
    /// the counter never sees. That unattributed overhead is charged against the tail's
    /// allowance, so the budget is enforced even when history is not what is blowing it.
    /// If the overhead alone exceeds the budget, every turn compacts down to the newest
    /// message and stays there: the budget is unreachable, and raising it (or shortening
    /// the instructions) is the only fix.
    /// </remarks>
    private (bool Over, int KeepBudget) EvaluateTokenBudget(Conversation conversation)
    {
        if (_options.MaxHotTokens is not int budget)
        {
            return (false, 0);
        }

        int estimated = _options.TokenCounter.CountTokens(conversation.Messages);
        long observed = conversation.LastInputTokenCount ?? estimated;
        if (observed <= budget)
        {
            return (false, 0);
        }

        int keep = _options.KeepRecentTokens ?? Math.Max(1, budget / 3);
        long overhead = Math.Max(0, observed - estimated);
        return (true, (int)Math.Max(0, keep - overhead));
    }

    /// <summary>
    /// The earliest boundary whose retained tail fits <paramref name="keepBudget"/>. Walks
    /// backwards from the newest message so the most recent turns are always the ones kept.
    /// </summary>
    private int TokenBoundary(IReadOnlyList<ChatMessage> messages, int keepBudget)
    {
        int tail = 0;
        int index = messages.Count - 1;
        for (; index >= 0; index--)
        {
            int cost = _options.TokenCounter.CountTokens(messages[index]);
            if (tail + cost > keepBudget)
            {
                break;
            }
            tail += cost;
        }
        // index is the last message that did NOT fit, so it and everything older is evicted.
        // Always keep at least the newest message, even when it alone exceeds the budget.
        return Math.Min(index + 1, messages.Count - 1);
    }

    /// <summary>
    /// Advances the eviction boundary while the first message that would remain hot is a
    /// tool result, so a call/result chain is evicted (and summarized) as a unit instead
    /// of leaving an orphaned tool result that providers reject.
    /// </summary>
    private static int AdjustBoundaryPastToolChains(IReadOnlyList<ChatMessage> messages, int boundary)
    {
        while (boundary < messages.Count
            && messages[boundary].Contents.Any(c => c is FunctionResultContent))
        {
            boundary++;
        }
        return boundary;
    }
}
