using Microsoft.Extensions.AI;
using System.Text.Json.Serialization;
using System.Text.Json;
using Trellis.Conversations;
using Trellis.State;

namespace Trellis.Conversations.Storage;

/// <summary>The serialized shape of a stored conversation.</summary>
internal sealed record ConversationSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("epoch")] int ContextEpoch,
    [property: JsonPropertyName("archived")] int ArchivedCount,
    [property: JsonPropertyName("lastInputTokens")] long? LastInputTokenCount);
