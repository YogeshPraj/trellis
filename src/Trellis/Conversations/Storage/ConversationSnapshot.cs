using Microsoft.Extensions.AI;
using System.Text.Json.Serialization;
using System.Text.Json;
using Trellis.Conversations;
using Trellis.State;

namespace Trellis.Conversations.Storage;

/// <summary>
/// The complete persisted state of a conversation — everything an
/// <see cref="IConversationStore"/> must round-trip. Public because store providers live in
/// their own assemblies (Redis, Cosmos, and whatever you write next).
/// </summary>
public sealed record ConversationSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("epoch")] int ContextEpoch,
    [property: JsonPropertyName("archived")] int ArchivedCount,
    [property: JsonPropertyName("lastInputTokens")] long? LastInputTokenCount);
