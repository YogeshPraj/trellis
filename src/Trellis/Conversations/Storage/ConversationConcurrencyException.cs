namespace Trellis.Conversations.Storage;

/// <summary>
/// Another instance wrote this conversation after the local copy was loaded. Saving the
/// local copy would silently discard that turn, so the write is refused instead.
/// </summary>
/// <remarks>
/// Recover by reloading the conversation and replaying the user's turn against it. Seeing
/// these regularly means two instances are serving one conversation concurrently — add
/// session affinity rather than retrying harder.
/// </remarks>
public sealed class ConversationConcurrencyException(string conversationId, int expectedVersion, int actualVersion)
    : Exception($"Conversation '{conversationId}' was modified by another writer " +
                $"(expected version {expectedVersion}, found {actualVersion}). Reload and reapply the turn.")
{
    public string ConversationId { get; } = conversationId;

    /// <summary>The version the local copy was based on.</summary>
    public int ExpectedVersion { get; } = expectedVersion;

    /// <summary>The version actually in the store.</summary>
    public int ActualVersion { get; } = actualVersion;
}
