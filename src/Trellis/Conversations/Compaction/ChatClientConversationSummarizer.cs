using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text;
using Trellis.Conversations.Archive;
using Trellis.Conversations;
using Trellis.State;
using Trellis.Tokens;

namespace Trellis.Conversations.Compaction;

/// <summary>
/// Default summarizer: asks any <see cref="IChatClient"/> to fold the evicted turns into
/// the running summary. Point it at a small, cheap model — summaries don't need your best one.
/// </summary>
/// <remarks>
/// The summary is bounded: a rolling summary that grows by a paragraph per compaction would
/// silently re-inflate every prompt over weeks of conversation, defeating the point. The
/// model is told the budget, and the result is hard-truncated if it overshoots anyway.
/// </remarks>
public sealed class ChatClientConversationSummarizer : IConversationSummarizer
{
    private readonly IChatClient _client;
    private readonly int _maxSummaryCharacters;

    /// <param name="client">The (ideally small and cheap) model that writes summaries.</param>
    /// <param name="maxSummaryCharacters">
    /// Hard ceiling on the rolling summary, so it can never grow without bound (default 4000).
    /// </param>
    public ChatClientConversationSummarizer(IChatClient client, int maxSummaryCharacters = 4000)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSummaryCharacters, 1);
        _client = client;
        _maxSummaryCharacters = maxSummaryCharacters;
    }

    public async Task<string> SummarizeAsync(
        string? existingSummary,
        IReadOnlyList<ChatMessage> evictedMessages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evictedMessages);

        var prompt = new StringBuilder()
            .AppendLine("You maintain the running summary of an ongoing conversation.")
            .AppendLine("Update the summary to incorporate the new turns below. Preserve key facts,")
            .AppendLine("decisions, names, numbers, and open questions. Reply with ONLY the updated summary.")
            .Append("Keep it under ").Append(_maxSummaryCharacters).AppendLine(" characters — drop the")
            .AppendLine("least important details rather than exceeding it.")
            .AppendLine()
            .AppendLine("Current summary:")
            .AppendLine(string.IsNullOrEmpty(existingSummary) ? "(none)" : existingSummary)
            .AppendLine()
            .AppendLine("New turns:");
        foreach (ChatMessage message in evictedMessages)
        {
            prompt.Append(message.Role.Value).Append(": ").AppendLine(message.Text);
        }

        ChatResponse response = await _client
            .GetResponseAsync([new ChatMessage(ChatRole.User, prompt.ToString())], null, cancellationToken)
            .ConfigureAwait(false);

        // Instructions are a request, not a guarantee — enforce the ceiling ourselves.
        string summary = response.Text;
        return summary.Length <= _maxSummaryCharacters ? summary : summary[.._maxSummaryCharacters];
    }
}
