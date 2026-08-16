using Microsoft.Extensions.AI;
using System.Text.Json.Serialization;
using System.Text.Json;
using Trellis.Conversations;
using Trellis.State;

namespace Trellis.Conversations.Storage;

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
public sealed class SharedStateConversationStore : IReplicatedConversationStore
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

    public async ValueTask<int?> GetVersionAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        string? json = await _store.GetAsync(_keyPrefix + conversationId, cancellationToken).ConfigureAwait(false);
        return json is null ? null : ConversationSerializer.Deserialize(json)?.Version;
    }

    public ValueTask ReplaceAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return _store.SetAsync(
            _keyPrefix + snapshot.Id, ConversationSerializer.SerializeSnapshot(snapshot), _timeToLive, cancellationToken);
    }

    public ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        return _store.RemoveAsync(_keyPrefix + conversationId, cancellationToken);
    }
}
