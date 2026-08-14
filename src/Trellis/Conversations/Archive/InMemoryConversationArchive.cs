using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text;
using Trellis.Conversations;
using Trellis.State;

namespace Trellis.Conversations.Archive;

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
