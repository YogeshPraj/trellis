using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Trellis.State;

namespace Trellis;

/// <summary>When and how much hot context to keep.</summary>
public sealed class CompactionOptions
{
    /// <summary>Compaction triggers once the hot history exceeds this many messages.</summary>
    public int MaxHotMessages { get; set; } = 40;

    /// <summary>How many recent messages stay hot (verbatim) after a compaction.</summary>
    public int KeepRecentMessages { get; set; } = 12;

    /// <summary>
    /// Called when a compaction attempt fails (summarizer or archive error). Compaction
    /// failures never fail the user's turn — the turn proceeds uncompacted and compaction
    /// is retried on a later turn. Use this hook for logging/alerting.
    /// </summary>
    public Action<Exception>? OnCompactionFailure { get; set; }
}

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

/// <summary>
/// Default summarizer: asks any <see cref="IChatClient"/> to fold the evicted turns into
/// the running summary. Point it at a small, cheap model — summaries don't need your best one.
/// </summary>
public sealed class ChatClientConversationSummarizer : IConversationSummarizer
{
    private readonly IChatClient _client;

    public ChatClientConversationSummarizer(IChatClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
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
        return response.Text;
    }
}

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

/// <summary>In-process archive. Suitable for single-instance apps and tests.</summary>
public sealed class InMemoryConversationArchive : IConversationArchive
{
    private readonly Dictionary<string, List<ChatMessage>> _cold = [];
    private readonly Lock _lock = new();

    public ValueTask ArchiveAsync(
        string conversationId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(messages);
        lock (_lock)
        {
            if (!_cold.TryGetValue(conversationId, out List<ChatMessage>? list))
            {
                _cold[conversationId] = list = [];
            }
            list.AddRange(messages);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<ChatMessage>> LoadAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        lock (_lock)
        {
            return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(
                _cold.TryGetValue(conversationId, out List<ChatMessage>? list) ? [.. list] : []);
        }
    }
}

/// <summary>
/// Archive provider over any <see cref="ISharedStateStore"/> (Redis, IDistributedCache, ...),
/// so cold context survives restarts and is shared across app instances. Each message is
/// appended individually via the store's <see cref="ISharedStateStore.AppendAsync"/>, so with
/// an atomic provider (Redis) concurrent archivers cannot lose messages. With the
/// IDistributedCache bridge, appends are read-modify-write — see that provider's atomicity note.
/// </summary>
public sealed class SharedStateConversationArchive : IConversationArchive
{
    private readonly ISharedStateStore _store;
    private readonly string _keyPrefix;

    public SharedStateConversationArchive(ISharedStateStore store, string keyPrefix = "conversation-archive:")
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keyPrefix);
        _store = store;
        _keyPrefix = keyPrefix;
    }

    public async ValueTask ArchiveAsync(
        string conversationId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(messages);
        foreach (ChatMessage message in messages)
        {
            string json = JsonSerializer.Serialize(message, AIJsonUtilities.DefaultOptions);
            await _store.AppendAsync(_keyPrefix + conversationId, json, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<IReadOnlyList<ChatMessage>> LoadAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        IReadOnlyList<string> entries = await _store
            .GetListAsync(_keyPrefix + conversationId, cancellationToken)
            .ConfigureAwait(false);
        List<ChatMessage> messages = new(entries.Count);
        foreach (string json in entries)
        {
            if (JsonSerializer.Deserialize<ChatMessage>(json, AIJsonUtilities.DefaultOptions) is ChatMessage message)
            {
                messages.Add(message);
            }
        }
        return messages;
    }
}

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
        if (conversation.Messages.Count <= _options.MaxHotMessages)
        {
            return false;
        }

        int evictCount = AdjustBoundaryPastToolChains(conversation.Messages, conversation.Messages.Count - _options.KeepRecentMessages);
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
