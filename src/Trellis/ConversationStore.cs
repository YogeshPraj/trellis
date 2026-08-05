using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Trellis.State;

namespace Trellis;

/// <summary>
/// Persists live (hot) conversations so consecutive turns can land on different instances.
/// The archive (<see cref="IConversationArchive"/>) holds cold, compacted history; this
/// holds the working conversation itself — hot messages, rolling summary, context epoch.
/// </summary>
public interface IConversationStore
{
    /// <summary>Loads a conversation, or null when the id is unknown.</summary>
    ValueTask<Conversation?> LoadAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a conversation, rejecting the write with
    /// <see cref="ConversationConcurrencyException"/> when another writer has advanced it
    /// since this copy was loaded. On success the conversation's
    /// <see cref="Conversation.Version"/> moves forward.
    /// </summary>
    ValueTask SaveAsync(Conversation conversation, CancellationToken cancellationToken = default);

    /// <summary>Deletes a conversation. Deleting an unknown id is not an error.</summary>
    ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default);
}

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

/// <summary>The serialized shape of a stored conversation.</summary>
internal sealed record ConversationSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("epoch")] int ContextEpoch,
    [property: JsonPropertyName("archived")] int ArchivedCount,
    [property: JsonPropertyName("lastInputTokens")] long? LastInputTokenCount);

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

/// <summary>
/// In-process store. Useful for tests and single-instance apps; it still enforces version
/// checks, so code written against it behaves the same on a distributed backend.
/// </summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly Dictionary<string, string> _conversations = [];
    private readonly Lock _lock = new();

    public ValueTask<Conversation?> LoadAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        lock (_lock)
        {
            if (!_conversations.TryGetValue(conversationId, out string? json))
            {
                return ValueTask.FromResult<Conversation?>(null);
            }
            ConversationSnapshot? snapshot = ConversationSerializer.Deserialize(json);
            return ValueTask.FromResult(snapshot is null ? null : ConversationSerializer.ToConversation(snapshot));
        }
    }

    public ValueTask SaveAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        lock (_lock)
        {
            int stored = _conversations.TryGetValue(conversation.Id, out string? json)
                ? ConversationSerializer.Deserialize(json)?.Version ?? 0
                : 0;
            if (stored != conversation.Version)
            {
                throw new ConversationConcurrencyException(conversation.Id, conversation.Version, stored);
            }

            int next = conversation.Version + 1;
            _conversations[conversation.Id] = ConversationSerializer.Serialize(conversation, next);
            conversation.MarkPersisted(next);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        lock (_lock)
        {
            _conversations.Remove(conversationId);
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Conversation store over any <see cref="ISharedStateStore"/> (Redis, IDistributedCache, ...),
/// so a conversation survives restarts and consecutive turns can be served by different
/// instances.
/// </summary>
/// <remarks>
/// <para>
/// Lost-update protection depends on the backend. When the store also implements
/// <see cref="IAtomicSharedStateStore"/> (in-memory, Redis) the save is a genuine
/// compare-and-swap and a concurrent writer is rejected with
/// <see cref="ConversationConcurrencyException"/>. Without it (the IDistributedCache
/// bridge) the version check still catches the common case, but read-modify-write leaves a
/// narrow race — set <c>requireAtomicStore</c> to refuse that backend outright rather than
/// inherit a silent last-write-wins.
/// </para>
/// <para>
/// A TTL is strongly recommended: conversations that are abandoned mid-session are never
/// deleted explicitly, and without expiry they accumulate forever.
/// </para>
/// </remarks>
public sealed class SharedStateConversationStore : IConversationStore
{
    private readonly ISharedStateStore _store;
    private readonly IAtomicSharedStateStore? _atomic;
    private readonly string _keyPrefix;
    private readonly TimeSpan? _timeToLive;

    /// <param name="store">The backing shared-state provider.</param>
    /// <param name="timeToLive">Expiry for stored conversations; null keeps them forever.</param>
    /// <param name="keyPrefix">Namespace for conversation keys.</param>
    /// <param name="requireAtomicStore">
    /// When true, refuse a backend that cannot compare-and-swap instead of silently
    /// degrading to last-write-wins.
    /// </param>
    public SharedStateConversationStore(
        ISharedStateStore store,
        TimeSpan? timeToLive = null,
        string keyPrefix = "conversation:",
        bool requireAtomicStore = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keyPrefix);
        _store = store;
        _atomic = store as IAtomicSharedStateStore;
        if (requireAtomicStore && _atomic is null)
        {
            throw new ArgumentException(
                $"{store.GetType().Name} does not implement {nameof(IAtomicSharedStateStore)}, so concurrent " +
                "writers cannot be detected reliably. Use an atomic provider (Redis) or set " +
                "requireAtomicStore to false to accept last-write-wins.",
                nameof(store));
        }
        _keyPrefix = keyPrefix;
        _timeToLive = timeToLive;
    }

    /// <summary>Whether saves are protected by a real compare-and-swap on this backend.</summary>
    public bool IsAtomic => _atomic is not null;

    public async ValueTask<Conversation?> LoadAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        string? json = await _store.GetAsync(_keyPrefix + conversationId, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }
        ConversationSnapshot? snapshot = ConversationSerializer.Deserialize(json);
        return snapshot is null ? null : ConversationSerializer.ToConversation(snapshot);
    }

    public async ValueTask SaveAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        string key = _keyPrefix + conversation.Id;

        // Read the current value first: it is both the version check and the compare-and-swap
        // token, so nothing can slip in between this read and the write below.
        string? current = await _store.GetAsync(key, cancellationToken).ConfigureAwait(false);
        int stored = current is null ? 0 : ConversationSerializer.Deserialize(current)?.Version ?? 0;
        if (stored != conversation.Version)
        {
            throw new ConversationConcurrencyException(conversation.Id, conversation.Version, stored);
        }

        int next = conversation.Version + 1;
        string json = ConversationSerializer.Serialize(conversation, next);

        if (_atomic is not null)
        {
            bool swapped = await _atomic
                .TrySetIfUnchangedAsync(key, current, json, _timeToLive, cancellationToken)
                .ConfigureAwait(false);
            if (!swapped)
            {
                throw new ConversationConcurrencyException(conversation.Id, conversation.Version, stored);
            }
        }
        else
        {
            await _store.SetAsync(key, json, _timeToLive, cancellationToken).ConfigureAwait(false);
        }

        conversation.MarkPersisted(next);
    }

    public ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        return _store.RemoveAsync(_keyPrefix + conversationId, cancellationToken);
    }
}
