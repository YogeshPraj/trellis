using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text;
using Trellis.Conversations;
using Trellis.State;

namespace Trellis.Conversations.Archive;

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
