using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Trellis.Conversations;
using Trellis.Conversations.Storage;

namespace Trellis.Azure.Cosmos;

/// <summary>
/// Cosmos-native conversation storage: one partition per conversation, one immutable
/// document per message, and a small head document carrying version and counters.
/// </summary>
/// <remarks>
/// <para><b>Why not store the conversation as one document</b></para>
/// <para>
/// Rewriting the whole history every turn costs request units proportional to the entire
/// conversation, so turn 100 costs roughly a hundred times turn 1 — and the 2&#160;MB
/// document limit eventually ends the conversation outright. Here a turn appends only its new
/// messages and patches a small head, so cost is flat in conversation length and there is no
/// ceiling.
/// </para>
/// <para><b>How a save commits</b></para>
/// <para>
/// Appends and the version bump go into a single <see cref="TransactionalBatch"/> against the
/// conversation's partition, with the head patched under an ETag precondition. The whole turn
/// commits or none of it does, and a concurrent writer loses the precondition rather than
/// interleaving. Message ids are deterministic (<c>m-{ordinal}</c>), so a replayed save
/// conflicts instead of duplicating, and <see cref="CosmosConversationHead.MessageCount"/> is
/// the commit point — messages appended by a save that never committed are simply never read.
/// </para>
/// <para><b>Container requirements</b></para>
/// <list type="bullet">
/// <item>Partition key path <c>/cid</c>.</item>
/// <item><c>DefaultTimeToLive</c> configured if you pass a <c>timeToLive</c>, otherwise Cosmos
/// ignores per-item expiry silently — this store throws instead.</item>
/// </list>
/// </remarks>
public sealed class CosmosConversationStore : IConversationStore
{
    private const string HeadId = "head";
    private const string MessagePrefix = "m-";

    /// <summary>A batch is capped at 100 operations; the head patch takes one of them.</summary>
    private const int MaxAppendsPerBatch = 99;

    private readonly Container _container;
    private readonly TimeSpan? _timeToLive;
    private readonly bool _timeToLiveEnabled;

    /// <param name="container">Container partitioned on <c>/cid</c>; the caller owns its lifetime.</param>
    /// <param name="timeToLive">Expiry for a conversation's documents; null keeps them forever.</param>
    /// <param name="timeToLiveEnabled">
    /// Whether the container has <c>DefaultTimeToLive</c> set. Leave true if it does; false
    /// makes a TTL request fail loudly rather than be dropped by Cosmos.
    /// </param>
    public CosmosConversationStore(Container container, TimeSpan? timeToLive = null, bool timeToLiveEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (timeToLive is not null && !timeToLiveEnabled)
        {
            throw new ArgumentException(
                "A time-to-live was requested but the container has no DefaultTimeToLive, so Cosmos would " +
                "silently ignore it. Set DefaultTimeToLive on the container (-1 enables per-item TTL).",
                nameof(timeToLive));
        }
        _container = container;
        _timeToLive = timeToLive;
        _timeToLiveEnabled = timeToLiveEnabled;
    }

    public async ValueTask<Conversation?> LoadAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        (CosmosConversationHead? head, _) = await ReadHeadAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return null;
        }

        // Only the hot tail is read: everything below ArchivedCount was compacted away, and
        // anything at or above MessageCount belongs to a save that never committed.
        List<ChatMessage> messages = await ReadMessagesAsync(
            conversationId, head.ArchivedCount, head.MessageCount, cancellationToken).ConfigureAwait(false);

        return Conversation.FromSnapshot(new ConversationSnapshot(
            head.ConversationId,
            head.Version,
            messages,
            head.Summary,
            head.ContextEpoch,
            head.ArchivedCount,
            head.LastInputTokenCount));
    }

    public async ValueTask SaveAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        string conversationId = conversation.Id;

        (CosmosConversationHead? head, string? etag) =
            await ReadHeadAsync(conversationId, cancellationToken).ConfigureAwait(false);

        int storedVersion = head?.Version ?? 0;
        if (storedVersion != conversation.Version)
        {
            throw new ConversationConcurrencyException(conversationId, conversation.Version, storedVersion);
        }

        int nextVersion = conversation.Version + 1;
        int committed = head?.MessageCount ?? 0;
        int total = conversation.ArchivedCount + conversation.Messages.Count;

        // Messages whose ordinal we no longer hold — compacted away before they were ever
        // persisted — cannot be appended; the summary is what carries them from here.
        int firstAppendable = Math.Max(committed, conversation.ArchivedCount);
        List<CosmosConversationMessage> appends = [];
        for (int ordinal = firstAppendable; ordinal < total; ordinal++)
        {
            ChatMessage message = conversation.Messages[ordinal - conversation.ArchivedCount];
            appends.Add(new CosmosConversationMessage
            {
                Id = MessageId(ordinal),
                ConversationId = conversationId,
                Ordinal = ordinal,
                Message = JsonSerializer.Serialize(message, AIJsonUtilities.DefaultOptions),
                TimeToLiveSeconds = TimeToLiveSeconds(),
            });
        }

        // Appends beyond one batch are committed in idempotent chunks; the head patch in the
        // final batch is the commit point, so a crash midway leaves unreferenced messages
        // that the next attempt re-creates harmlessly and no reader ever sees.
        for (int offset = 0; offset < appends.Count - MaxAppendsPerBatch; offset += MaxAppendsPerBatch)
        {
            await ExecuteAsync(
                conversationId,
                appends.Skip(offset).Take(MaxAppendsPerBatch),
                head: null, etag: null, nextVersion, total, conversation, cancellationToken).ConfigureAwait(false);
        }

        IEnumerable<CosmosConversationMessage> finalChunk = appends.Count > MaxAppendsPerBatch
            ? appends.Skip((appends.Count - 1) / MaxAppendsPerBatch * MaxAppendsPerBatch)
            : appends;

        await ExecuteAsync(conversationId, finalChunk, head, etag, nextVersion, total, conversation, cancellationToken)
            .ConfigureAwait(false);

        conversation.MarkPersisted(nextVersion);
    }

    public async ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        var partition = new PartitionKey(conversationId);

        (CosmosConversationHead? head, _) = await ReadHeadAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return;
        }

        // Head first: once it is gone the conversation reads as absent, so a failure partway
        // through leaves unreachable documents rather than a half-readable conversation.
        await DeleteIfPresentAsync(HeadId, partition, cancellationToken).ConfigureAwait(false);
        for (int ordinal = 0; ordinal < head.MessageCount; ordinal++)
        {
            await DeleteIfPresentAsync(MessageId(ordinal), partition, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteAsync(
        string conversationId,
        IEnumerable<CosmosConversationMessage> appends,
        CosmosConversationHead? head,
        string? etag,
        int nextVersion,
        int total,
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        var partition = new PartitionKey(conversationId);
        TransactionalBatch batch = _container.CreateTransactionalBatch(partition);
        bool any = false;

        foreach (CosmosConversationMessage message in appends)
        {
            batch.CreateItem(message);
            any = true;
        }

        bool commit = head is not null || etag is null;
        if (commit)
        {
            if (head is null)
            {
                batch.CreateItem(NewHead(conversationId, nextVersion, total, conversation));
            }
            else
            {
                batch.PatchItem(
                    HeadId,
                    HeadPatch(nextVersion, total, conversation),
                    new TransactionalBatchPatchItemRequestOptions { IfMatchEtag = etag });
            }
            any = true;
        }

        if (!any)
        {
            return;
        }

        using TransactionalBatchResponse response = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // A lost ETag race, or a message id that already exists because another writer got
        // there first: both mean this copy was stale.
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            (CosmosConversationHead? latest, _) = await ReadHeadAsync(conversationId, cancellationToken)
                .ConfigureAwait(false);
            throw new ConversationConcurrencyException(conversationId, conversation.Version, latest?.Version ?? 0);
        }

        throw new CosmosException(
            $"Saving conversation '{conversationId}' failed: {response.ErrorMessage}",
            response.StatusCode, 0, response.ActivityId, response.RequestCharge);
    }

    /// <summary>Only the fields a turn can change, so the request is charged on the change.</summary>
    private List<PatchOperation> HeadPatch(int nextVersion, int total, Conversation conversation) =>
    [
        PatchOperation.Set("/version", nextVersion),
        PatchOperation.Set("/messageCount", total),
        PatchOperation.Set("/archived", conversation.ArchivedCount),
        PatchOperation.Set("/epoch", conversation.ContextEpoch),
        PatchOperation.Set("/summary", conversation.Summary),
        PatchOperation.Set("/lastInputTokens", conversation.LastInputTokenCount),
    ];

    private CosmosConversationHead NewHead(string conversationId, int version, int total, Conversation conversation) =>
        new()
        {
            Id = HeadId,
            ConversationId = conversationId,
            Version = version,
            MessageCount = total,
            ArchivedCount = conversation.ArchivedCount,
            ContextEpoch = conversation.ContextEpoch,
            Summary = conversation.Summary,
            LastInputTokenCount = conversation.LastInputTokenCount,
            TimeToLiveSeconds = TimeToLiveSeconds(),
        };

    private async ValueTask<(CosmosConversationHead? Head, string? ETag)> ReadHeadAsync(
        string conversationId, CancellationToken cancellationToken)
    {
        try
        {
            ItemResponse<CosmosConversationHead> response = await _container
                .ReadItemAsync<CosmosConversationHead>(HeadId, new PartitionKey(conversationId), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return (response.Resource, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }
    }

    private async Task<List<ChatMessage>> ReadMessagesAsync(
        string conversationId, int fromOrdinal, int toOrdinal, CancellationToken cancellationToken)
    {
        List<ChatMessage> messages = [];
        if (toOrdinal <= fromOrdinal)
        {
            return messages;
        }

        var query = new QueryDefinition(
            "SELECT c.message FROM c WHERE c.cid = @cid AND c.ordinal >= @from AND c.ordinal < @to ORDER BY c.ordinal")
            .WithParameter("@cid", conversationId)
            .WithParameter("@from", fromOrdinal)
            .WithParameter("@to", toOrdinal);

        using FeedIterator<CosmosMessageProjection> iterator = _container.GetItemQueryIterator<CosmosMessageProjection>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(conversationId) });

        while (iterator.HasMoreResults)
        {
            foreach (CosmosMessageProjection item in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item.Message is not null
                    && JsonSerializer.Deserialize<ChatMessage>(item.Message, AIJsonUtilities.DefaultOptions) is { } message)
                {
                    messages.Add(message);
                }
            }
        }
        return messages;
    }

    private async Task DeleteIfPresentAsync(string id, PartitionKey partition, CancellationToken cancellationToken)
    {
        try
        {
            await _container.DeleteItemAsync<object>(id, partition, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private int? TimeToLiveSeconds() =>
        _timeToLive is TimeSpan ttl && _timeToLiveEnabled ? Math.Max(1, (int)ttl.TotalSeconds) : null;

    /// <summary>Zero-padded so ids sort in ordinal order, and deterministic so appends are idempotent.</summary>
    private static string MessageId(int ordinal) =>
        MessagePrefix + ordinal.ToString("D9", CultureInfo.InvariantCulture);
}

/// <summary>Query projection for reading a conversation's messages.</summary>
public sealed class CosmosMessageProjection
{
    [Newtonsoft.Json.JsonProperty("message")]
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string? Message { get; set; }
}
