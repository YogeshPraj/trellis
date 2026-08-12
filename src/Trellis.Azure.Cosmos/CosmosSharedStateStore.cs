using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Trellis.State;

namespace Trellis.Azure.Cosmos;

/// <summary>
/// Azure Cosmos DB provider for <see cref="ISharedStateStore"/> — the durable tier for
/// conversations, router health, and archives.
/// </summary>
/// <remarks>
/// <para><b>Container requirements</b></para>
/// <list type="bullet">
/// <item>Partition key path must be <c>/pk</c>. Every logical key is its own partition, so
/// reads and list queries are single-partition and cheap.</item>
/// <item>To use <c>timeToLive</c>, the container's <c>DefaultTimeToLive</c> must be set
/// (<c>-1</c> enables per-item TTL without expiring anything by default). Without it, Cosmos
/// silently ignores per-item TTL — so this store throws rather than pretend expiry works.</item>
/// </list>
/// <para><b>Atomicity</b></para>
/// <para>
/// Compare-and-swap uses Cosmos ETags, so <see cref="IAtomicSharedStateStore"/> is honoured
/// across instances: a concurrent writer causes a 412 and the swap reports false. Increments
/// use the server-side Patch <c>Increment</c> operation. Appends write one document per
/// entry rather than growing an array, so a list is not capped by the 2&#160;MB document
/// limit — the cost is that <see cref="GetListAsync"/> is a query, not a point read.
/// </para>
/// <para>
/// The application owns the <see cref="Container"/>'s lifetime; this store never creates or
/// disposes a <c>CosmosClient</c>.
/// </para>
/// </remarks>
public sealed class CosmosSharedStateStore : IAtomicSharedStateStore
{
    /// <summary>Fixed id of the scalar document within a key's partition.</summary>
    private const string ValueDocumentId = "value";

    /// <summary>Prefix for list-entry document ids, which sort chronologically after it.</summary>
    private const string ListEntryPrefix = "e-";

    private readonly Container _container;
    private readonly string _keyPrefix;
    private readonly bool _timeToLiveEnabled;

    /// <param name="container">
    /// The Cosmos container, partitioned on <c>/pk</c>. Its lifetime belongs to the caller.
    /// </param>
    /// <param name="keyPrefix">Prepended to every key so Trellis state is namespaced.</param>
    /// <param name="timeToLiveEnabled">
    /// Whether the container has <c>DefaultTimeToLive</c> configured. Leave true (the
    /// default) if it does; set false to make TTL requests fail loudly instead of being
    /// silently dropped by Cosmos.
    /// </param>
    public CosmosSharedStateStore(Container container, string keyPrefix = "trellis:", bool timeToLiveEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(keyPrefix);
        _container = container;
        _keyPrefix = keyPrefix;
        _timeToLiveEnabled = timeToLiveEnabled;
    }

    public async ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        (CosmosStateDocument? document, _) = await ReadAsync(key, cancellationToken).ConfigureAwait(false);
        return document?.Payload;
    }

    public async ValueTask SetAsync(
        string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        string partition = Partition(key);
        await _container.UpsertItemAsync(
            NewValueDocument(partition, value, timeToLive),
            new PartitionKey(partition),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        string partition = Partition(key);

        try
        {
            await _container.DeleteItemAsync<CosmosStateDocument>(
                ValueDocumentId, new PartitionKey(partition), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Removing something that isn't there is not an error.
        }

        // A key may hold a list instead of (or as well as) a scalar; clear both so Remove
        // means "this key is gone", matching the in-memory and Redis providers.
        foreach (string id in await ListEntryIdsAsync(partition, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await _container.DeleteItemAsync<CosmosStateDocument>(
                    id, new PartitionKey(partition), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }
    }

    /// <summary>Atomic across instances: the server-side Patch <c>Increment</c> operation.</summary>
    public async ValueTask<long> IncrementAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        string partition = Partition(key);

        try
        {
            ItemResponse<CosmosCounterDocument> patched = await _container.PatchItemAsync<CosmosCounterDocument>(
                ValueDocumentId,
                new PartitionKey(partition),
                [PatchOperation.Increment("/counter", 1)],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return patched.Resource.Counter;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            // First increment for this key: create the counter. A racing creator wins with
            // 409, and we patch the document it created rather than clobbering its value.
            try
            {
                ItemResponse<CosmosCounterDocument> created = await _container.CreateItemAsync(
                    new CosmosCounterDocument { Id = ValueDocumentId, PartitionKey = partition, Counter = 1, Payload = "1" },
                    new PartitionKey(partition),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return created.Resource.Counter;
            }
            catch (CosmosException conflict) when (conflict.StatusCode == HttpStatusCode.Conflict)
            {
                ItemResponse<CosmosCounterDocument> patched = await _container.PatchItemAsync<CosmosCounterDocument>(
                    ValueDocumentId,
                    new PartitionKey(partition),
                    [PatchOperation.Increment("/counter", 1)],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return patched.Resource.Counter;
            }
        }
    }

    /// <summary>
    /// Atomic across instances: each entry is its own document, so concurrent appends cannot
    /// overwrite one another and the list is not bounded by the document size limit.
    /// </summary>
    public async ValueTask<long> AppendAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        string partition = Partition(key);

        await _container.CreateItemAsync(
            new CosmosStateDocument
            {
                Id = NewListEntryId(),
                PartitionKey = partition,
                Payload = value,
            },
            new PartitionKey(partition),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return await CountListEntriesAsync(partition, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<string>> GetListAsync(
        string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        string partition = Partition(key);

        var query = new QueryDefinition(
            "SELECT c.payload FROM c WHERE c.pk = @pk AND STARTSWITH(c.id, @prefix) ORDER BY c.id")
            .WithParameter("@pk", partition)
            .WithParameter("@prefix", ListEntryPrefix);

        List<string> values = [];
        using FeedIterator<CosmosPayloadProjection> iterator = _container.GetItemQueryIterator<CosmosPayloadProjection>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(partition) });

        while (iterator.HasMoreResults)
        {
            foreach (CosmosPayloadProjection item in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item.Payload is not null)
                {
                    values.Add(item.Payload);
                }
            }
        }
        return values;
    }

    /// <summary>
    /// Atomic across instances via Cosmos ETags: a concurrent writer changes the ETag, the
    /// conditional write fails with 412, and the swap reports false rather than overwriting.
    /// </summary>
    public async ValueTask<bool> TrySetIfUnchangedAsync(
        string key,
        string? expectedValue,
        string newValue,
        TimeSpan? timeToLive = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(newValue);

        string partition = Partition(key);
        (CosmosStateDocument? current, string? etag) = await ReadAsync(key, cancellationToken).ConfigureAwait(false);

        if (expectedValue is null)
        {
            if (current is not null)
            {
                return false;
            }
            try
            {
                await _container.CreateItemAsync(
                    NewValueDocument(partition, newValue, timeToLive),
                    new PartitionKey(partition),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                return false;   // someone created it between our read and our write
            }
        }

        if (current is null || current.Payload != expectedValue || etag is null)
        {
            return false;
        }

        try
        {
            await _container.ReplaceItemAsync(
                NewValueDocument(partition, newValue, timeToLive),
                ValueDocumentId,
                new PartitionKey(partition),
                new ItemRequestOptions { IfMatchEtag = etag },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async ValueTask<(CosmosStateDocument? Document, string? ETag)> ReadAsync(
        string key, CancellationToken cancellationToken)
    {
        string partition = Partition(key);
        try
        {
            ItemResponse<CosmosStateDocument> response = await _container.ReadItemAsync<CosmosStateDocument>(
                ValueDocumentId, new PartitionKey(partition), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return (response.Resource, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }
    }

    private async ValueTask<IReadOnlyList<string>> ListEntryIdsAsync(
        string partition, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT c.id FROM c WHERE c.pk = @pk AND STARTSWITH(c.id, @prefix)")
            .WithParameter("@pk", partition)
            .WithParameter("@prefix", ListEntryPrefix);

        List<string> ids = [];
        using FeedIterator<CosmosIdProjection> iterator = _container.GetItemQueryIterator<CosmosIdProjection>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(partition) });

        while (iterator.HasMoreResults)
        {
            foreach (CosmosIdProjection item in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item.Id is not null)
                {
                    ids.Add(item.Id);
                }
            }
        }
        return ids;
    }

    private async ValueTask<long> CountListEntriesAsync(string partition, CancellationToken cancellationToken) =>
        (await ListEntryIdsAsync(partition, cancellationToken).ConfigureAwait(false)).Count;

    private CosmosStateDocument NewValueDocument(string partition, string value, TimeSpan? timeToLive)
    {
        if (timeToLive is not null && !_timeToLiveEnabled)
        {
            throw new InvalidOperationException(
                "A time-to-live was requested but the container has no DefaultTimeToLive, so Cosmos would " +
                "silently ignore it. Set DefaultTimeToLive on the container (-1 enables per-item TTL), or " +
                "construct the store with timeToLiveEnabled: false and stop passing a TTL.");
        }

        return new CosmosStateDocument
        {
            Id = ValueDocumentId,
            PartitionKey = partition,
            Payload = value,
            TimeToLiveSeconds = timeToLive is TimeSpan ttl ? Math.Max(1, (int)ttl.TotalSeconds) : null,
        };
    }

    /// <summary>
    /// Chronologically sortable id: zero-padded ticks so lexicographic order matches time
    /// order, plus randomness to keep concurrent appends in the same tick distinct. Entries
    /// written within the same tick have an arbitrary relative order.
    /// </summary>
    private static string NewListEntryId() =>
        $"{ListEntryPrefix}{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}";

    private string Partition(string key) => _keyPrefix + key;
}
