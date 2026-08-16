using Microsoft.Extensions.AI;
using System.Text.Json.Serialization;
using System.Text.Json;
using Trellis.Conversations;
using Trellis.State;

namespace Trellis.Conversations.Storage;

/// <summary>Serialization shared by conversation store providers.</summary>
internal static class ConversationSerializer
{
    public static string Serialize(Conversation conversation, int version) =>
        JsonSerializer.Serialize(
            new ConversationSnapshot(
                conversation.Id,
                version,
                conversation.Messages,
                conversation.Summary,
                conversation.ContextEpoch,
                conversation.ArchivedCount,
                conversation.LastInputTokenCount),
            AIJsonUtilities.DefaultOptions);

    /// <summary>Serializes an already-built snapshot, for replication.</summary>
    public static string SerializeSnapshot(ConversationSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, AIJsonUtilities.DefaultOptions);

    public static ConversationSnapshot? Deserialize(string json) =>
        JsonSerializer.Deserialize<ConversationSnapshot>(json, AIJsonUtilities.DefaultOptions);

    public static Conversation ToConversation(ConversationSnapshot snapshot) =>
        Conversation.Restore(
            snapshot.Id,
            snapshot.Messages ?? [],
            snapshot.Summary,
            snapshot.ContextEpoch,
            snapshot.ArchivedCount,
            snapshot.LastInputTokenCount,
            snapshot.Version);
}
