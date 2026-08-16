namespace Trellis.Conversations.Storage;

/// <summary>
/// A conversation store that can also act as a tier of a <see cref="TieredConversationStore"/>.
/// </summary>
/// <remarks>
/// Replication needs two things ordinary application code never should. A replica must be
/// writable <em>unconditionally</em> — the authority has already decided the version, so a
/// second version check on the replica would reject a perfectly correct write — and its
/// version must be readable without paying for the whole conversation, so replication can
/// skip a tier that already holds something newer. Both are separated from
/// <see cref="IConversationStore"/> deliberately: application code should not be able to
/// overwrite a conversation without a version check.
/// </remarks>
public interface IReplicatedConversationStore : IConversationStore
{
    /// <summary>
    /// The version this store holds, or null when it does not have the conversation. Cheap by
    /// contract — implementations must not read the whole conversation to answer.
    /// </summary>
    ValueTask<int?> GetVersionAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Overwrites this store's copy with <paramref name="snapshot"/>, whatever it currently
    /// holds. For replication only; it performs no version check, so calling it from
    /// application code discards concurrent turns silently.
    /// </summary>
    ValueTask ReplaceAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken = default);
}
