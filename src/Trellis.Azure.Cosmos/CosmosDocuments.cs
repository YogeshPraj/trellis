using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace Trellis.Azure.Cosmos;

/// <summary>
/// The document <see cref="CosmosSharedStateStore"/> persists. Public because it is the
/// on-disk schema: you may want it when writing an indexing policy, a migration, or a query
/// against the container from outside Trellis.
/// </summary>
/// <remarks>
/// Every property carries BOTH Newtonsoft and System.Text.Json attributes on purpose. The
/// Cosmos SDK serializes with Newtonsoft unless the application installs a custom
/// <c>CosmosSerializer</c>, and a document that only round-tripped under one of them would be
/// silently corrupted under the other.
/// </remarks>
public class CosmosStateDocument
{
    /// <summary>Document id: <c>value</c> for a scalar, or <c>e-{sortable}</c> for a list entry.</summary>
    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Partition key — the prefixed logical key, so one key is one partition.</summary>
    [JsonProperty("pk")]
    [JsonPropertyName("pk")]
    public string PartitionKey { get; set; } = string.Empty;

    /// <summary>The stored string. Named <c>payload</c> because <c>value</c> is reserved in Cosmos SQL.</summary>
    [JsonProperty("payload")]
    [JsonPropertyName("payload")]
    public string? Payload { get; set; }

    /// <summary>Cosmos reads per-item expiry from a property literally named <c>ttl</c> (seconds).</summary>
    [JsonProperty("ttl", NullValueHandling = NullValueHandling.Ignore)]
    [JsonPropertyName("ttl")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeToLiveSeconds { get; set; }
}

/// <summary>A scalar document that also carries a counter, patched server-side by increments.</summary>
public sealed class CosmosCounterDocument : CosmosStateDocument
{
    [JsonProperty("counter")]
    [JsonPropertyName("counter")]
    public long Counter { get; set; }
}

/// <summary>Query projection for list reads.</summary>
public sealed class CosmosPayloadProjection
{
    [JsonProperty("payload")]
    [JsonPropertyName("payload")]
    public string? Payload { get; set; }
}

/// <summary>Query projection for id-only reads.</summary>
public sealed class CosmosIdProjection
{
    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
