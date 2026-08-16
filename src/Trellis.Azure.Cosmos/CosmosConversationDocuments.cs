using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace Trellis.Azure.Cosmos;

/// <summary>
/// The mutable head of a conversation: version, counters, and the rolling summary. Small on
/// purpose — it is the only document a turn touches more than once, and it is patched rather
/// than replaced so the request is charged on the change, not on the document.
/// </summary>
/// <remarks>
/// Carries both Newtonsoft and System.Text.Json attributes because the Cosmos SDK serializes
/// with Newtonsoft unless the application installs a custom <c>CosmosSerializer</c>.
/// </remarks>
public sealed class CosmosConversationHead
{
    /// <summary>Always <c>head</c>: one per conversation partition.</summary>
    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = "head";

    /// <summary>Partition key — the conversation id, so one conversation is one partition.</summary>
    [JsonProperty("cid")]
    [JsonPropertyName("cid")]
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Optimistic-concurrency version, bumped once per accepted save.</summary>
    [JsonProperty("version")]
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>
    /// How many messages have ever been committed. Ordinals below this are readable; anything
    /// above is an orphan from a save that failed after appending but before committing.
    /// </summary>
    [JsonProperty("messageCount")]
    [JsonPropertyName("messageCount")]
    public int MessageCount { get; set; }

    /// <summary>Messages evicted to cold storage; reads start from this ordinal.</summary>
    [JsonProperty("archived")]
    [JsonPropertyName("archived")]
    public int ArchivedCount { get; set; }

    /// <summary>Bumped per compaction; drives the conversation's routing id.</summary>
    [JsonProperty("epoch")]
    [JsonPropertyName("epoch")]
    public int ContextEpoch { get; set; }

    /// <summary>Rolling summary of compacted turns.</summary>
    [JsonProperty("summary")]
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    /// <summary>Provider-reported prompt size for the previous turn.</summary>
    [JsonProperty("lastInputTokens")]
    [JsonPropertyName("lastInputTokens")]
    public long? LastInputTokenCount { get; set; }

    [JsonProperty("ttl", NullValueHandling = NullValueHandling.Ignore)]
    [JsonPropertyName("ttl")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeToLiveSeconds { get; set; }
}

/// <summary>
/// One message of a conversation, written once and never modified. The id encodes the
/// message's global ordinal, so it sorts chronologically and — being deterministic — makes
/// an append idempotent: replaying a save conflicts (409) instead of duplicating.
/// </summary>
public sealed class CosmosConversationMessage
{
    /// <summary><c>m-</c> followed by the zero-padded ordinal, unique within the partition.</summary>
    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Partition key — the conversation id.</summary>
    [JsonProperty("cid")]
    [JsonPropertyName("cid")]
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Position of this message in the conversation, from zero.</summary>
    [JsonProperty("ordinal")]
    [JsonPropertyName("ordinal")]
    public int Ordinal { get; set; }

    /// <summary>The serialized <c>ChatMessage</c>, preserving tool call and result content.</summary>
    [JsonProperty("message")]
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("ttl", NullValueHandling = NullValueHandling.Ignore)]
    [JsonPropertyName("ttl")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeToLiveSeconds { get; set; }
}
