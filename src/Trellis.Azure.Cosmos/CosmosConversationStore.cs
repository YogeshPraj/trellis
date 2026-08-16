using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Trellis.Conversations;
using Trellis.Conversations.Storage;

namespace Trellis.Azure.Cosmos;

/// <summary>
/// Append-only Cosmos conversation storage: one partition per conversation, and every change
/// written as a new document. Nothing is ever replaced, updated, or patched.
/// </summary>
/// <remarks>
/// <para><b>Why append-only</b></para>
/// <para>
/// Keeping a conversation in one document means every turn rewrites the whole history —
/// request units proportional to conversation length, and a hard stop at the 2&#160;MB
/// document limit. Patching a header avoids the rewrite but is still a mutation, and Cosmos
/// caps a patch at ten operations. Writing only inserts sidesteps all of it: a turn costs
/// what its new content costs, and a conversation has no length ceiling.
/// </para>
/// <para><b>Concurrency without ETags</b></para>
/// <para>
/// A commit document's id is <c>v-{version}</c>, so committing version N+1 means inserting a
/// document only one writer can create. The loser gets a 409 and a
/// <see cref="ConversationConcurrencyException"/>. Optimistic concurrency falls out of the
/// unique key — no ETag, no read-modify-write, and the check is the same operation as the
/// commit.
/// </para>
/// <para><b>Layout of a partition</b></para>
/// <list type="bullet">
/// <item><c>m-{ordinal}</c> — one message, immutable. Deterministic id, so a replayed append
/// conflicts rather than duplicating.</item>
/// <item><c>v-{version}</c> — the metadata committed by one turn: counters, epoch, usage.
/// Small, and the newest one is the conversation's current state.</item>
/// <item><c>s-{epoch}</c> — a rolling summary, written only when compaction produces a new
/// one, so ordinary turns never rewrite it.</item>
/// </list>
/// <para>
/// A save that dies after appending messages but before its commit leaves documents no reader
/// will ever look at: reads are bounded by the newest commit's <c>messageCount</c>. Nothing
/// needs cleaning up, and the retry re-creates the same ids harmlessly.
/// </para>
/// <para><b>Container requirements:</b> partition key path <c>/cid</c>, and
/// <c>DefaultTimeToLive</c> configured if a <c>timeToLive</c> is supplied.</para>
/// </remarks>
public sealed class CosmosConversationStore : IConversationStore
{
    /// <summary>A transactional batch is capped at 100 operations; the commit takes one.</summary>
    private const int MaxAppendsPerBatch = 99;

    private readonly Container _container;
    private readonly TimeSpan? _timeToLive;

    /// <param name="container">Container partitioned on <c>/cid</c>; the caller owns its lifetime.</param>
    /// <param name="timeToLive">Expiry for a conversation's documents; null keeps them forever.</param>
    /// <param name="timeToLiveEnabled">
    /// Whether the container has <c>DefaultTimeToLive</c> set. False makes a TTL request fail
    /// loudly rather than be silently dropped by Cosmos.
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
    }

    public async ValueTask<Conversation?> LoadAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        CosmosConversationCommit? commit = await ReadLatestCommitAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);
        if (commit is null)
        {
            return null;
        }

        string? summary = commit.ContextEpoch > 0
            ? await ReadSummaryAsync(conversationId, commit.ContextEpoch, cancellationToken).ConfigureAwait(false)
            : null;

        List<ChatMessage> messages = await ReadMessagesAsync(
            conversationId, commit.ArchivedCount, commit.MessageCount, cancellationToken).ConfigureAwait(false);

        return Conversation.FromSnapshot(new ConversationSnapshot(
            conversationId, commit.Version, messages, summary,
            commit.ContextEpoch, commit.ArchivedCount, commit.LastInputTokenCount));
    }

    public async ValueTask SaveAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        string conversationId = conversation.Id;

        CosmosConversationCommit? latest = await ReadLatestCommitAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);
        int storedVersion = latest?.Version ?? 0;
        if (storedVersion != conversation.Version)
        {
            throw new ConversationConcurrencyException(conversationId, conversation.Version, storedVersion);
        }

        int nextVersion = conversation.Version + 1;
        int committed = latest?.MessageCount ?? 0;
        int total = conversation.ArchivedCount + conversation.Messages.Count;

        // Ordinals below ArchivedCount were compacted out of the live conversation, so if they
        // were never persisted they cannot be now — the summary is what carries them forward.
        int firstAppendable = Math.Max(committed, conversation.ArchivedCount);
        List<CosmosConversationDocument> appends = [];
        for (int ordinal = firstAppendable; ordinal < total; ordinal++)
        {
            appends.Add(new CosmosConversationMessage
            {
                Id = MessageId(ordinal),
                ConversationId = conversationId,
                Type = CosmosConversationDocumentTypes.Message,
                Ordinal = ordinal,
                Message = JsonSerializer.Serialize(
                    conversation.Messages[ordinal - conversation.ArchivedCount], AIJsonUtilities.DefaultOptions),
                TimeToLiveSeconds = TimeToLiveSeconds(),
            });
        }

        // Only a compaction produces a new summary, so ordinary turns write no summary at all.
        if (conversation.Summary is string summary && conversation.ContextEpoch > (latest?.ContextEpoch ?? 0))
        {
            appends.Add(new CosmosConversationSummary
            {
                Id = SummaryId(conversation.ContextEpoch),
                ConversationId = conversationId,
                Type = CosmosConversationDocumentTypes.Summary,
                ContextEpoch = conversation.ContextEpoch,
                Summary = summary,
                TimeToLiveSeconds = TimeToLiveSeconds(),
            });
        }

        var commit = new CosmosConversationCommit
        {
            Id = CommitId(nextVersion),
            ConversationId = conversationId,
            Type = CosmosConversationDocumentTypes.Commit,
            Version = nextVersion,
            MessageCount = total,
            ArchivedCount = conversation.ArchivedCount,
            ContextEpoch = conversation.ContextEpoch,
            LastInputTokenCount = conversation.LastInputTokenCount,
            TimeToLiveSeconds = TimeToLiveSeconds(),
        };

        // Appends beyond one batch go first, uncommitted; the batch carrying the commit
        // document is the point at which any of it becomes visible.
        for (int offset = 0; offset + MaxAppendsPerBatch < appends.Count; offset += MaxAppendsPerBatch)
        {
            await ExecuteAsync(
                conversationId, appends.Skip(offset).Take(MaxAppendsPerBatch), commit: null,
                conversation.Version, cancellationToken).ConfigureAwait(false);
        }

        int tail = appends.Count == 0 ? 0 : (appends.Count - 1) / MaxAppendsPerBatch * MaxAppendsPerBatch;
        await ExecuteAsync(conversationId, appends.Skip(tail), commit, conversation.Version, cancellationToken)
            .ConfigureAwait(false);

        conversation.MarkPersisted(nextVersion);
    }

    public async ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        var partition = new PartitionKey(conversationId);

        // Deleting is the one operation that cannot be an append. Every document id in the
        // partition is queried rather than derived, so orphans from uncommitted saves go too.
        var query = new QueryDefinition("SELECT c.id FROM c WHERE c.cid = @cid").WithParameter("@cid", conversationId);
        using FeedIterator<CosmosIdProjection> iterator = _container.GetItemQueryIterator<CosmosIdProjection>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = partition });

        while (iterator.HasMoreResults)
        {
            foreach (CosmosIdProjection item in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item.Id is null)
                {
                    continue;
                }
                try
                {
                    await _container.DeleteItemAsync<object>(item.Id, partition, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                }
            }
        }
    }

    private async Task ExecuteAsync(
        string conversationId,
        IEnumerable<CosmosConversationDocument> appends,
        CosmosConversationCommit? commit,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        TransactionalBatch batch = _container.CreateTransactionalBatch(new PartitionKey(conversationId));
        bool any = false;
        foreach (CosmosConversationDocument document in appends)
        {
            batch.CreateItem(document);
            any = true;
        }
        if (commit is not null)
        {
            batch.CreateItem(commit);
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

        // A 409 means a document we tried to create already exists — for the commit that is
        // exactly the concurrency check: another writer took this version first.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            CosmosConversationCommit? latest = await ReadLatestCommitAsync(conversationId, cancellationToken)
                .ConfigureAwait(false);
            throw new ConversationConcurrencyException(conversationId, expectedVersion, latest?.Version ?? 0);
        }

        throw new CosmosException(
            $"Saving conversation '{conversationId}' failed: {response.ErrorMessage}",
            response.StatusCode, 0, response.ActivityId, response.RequestCharge);
    }

    private async Task<CosmosConversationCommit?> ReadLatestCommitAsync(
        string conversationId, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT TOP 1 * FROM c WHERE c.cid = @cid AND c.type = @type ORDER BY c.version DESC")
            .WithParameter("@cid", conversationId)
            .WithParameter("@type", CosmosConversationDocumentTypes.Commit);

        using FeedIterator<CosmosConversationCommit> iterator = _container.GetItemQueryIterator<CosmosConversationCommit>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(conversationId) });

        while (iterator.HasMoreResults)
        {
            foreach (CosmosConversationCommit commit in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                return commit;
            }
        }
        return null;
    }

    private async Task<string?> ReadSummaryAsync(string conversationId, int epoch, CancellationToken cancellationToken)
    {
        try
        {
            ItemResponse<CosmosConversationSummary> response = await _container
                .ReadItemAsync<CosmosConversationSummary>(
                    SummaryId(epoch), new PartitionKey(conversationId), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return response.Resource?.Summary;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
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
            "SELECT c.message FROM c WHERE c.cid = @cid AND c.type = @type " +
            "AND c.ordinal >= @from AND c.ordinal < @to ORDER BY c.ordinal")
            .WithParameter("@cid", conversationId)
            .WithParameter("@type", CosmosConversationDocumentTypes.Message)
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

    private int? TimeToLiveSeconds() =>
        _timeToLive is TimeSpan ttl ? Math.Max(1, (int)ttl.TotalSeconds) : null;

    private static string MessageId(int ordinal) => "m-" + ordinal.ToString("D9", CultureInfo.InvariantCulture);

    private static string CommitId(int version) => "v-" + version.ToString("D9", CultureInfo.InvariantCulture);

    private static string SummaryId(int epoch) => "s-" + epoch.ToString("D9", CultureInfo.InvariantCulture);
}

/// <summary>Query projection for reading a conversation's messages.</summary>
public sealed class CosmosMessageProjection
{
    [Newtonsoft.Json.JsonProperty("message")]
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string? Message { get; set; }
}
