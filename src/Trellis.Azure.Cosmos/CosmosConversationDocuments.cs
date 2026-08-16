using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace Trellis.Azure.Cosmos;

/// <summary>Document kinds inside a conversation's partition.</summary>
public static class CosmosConversationDocumentTypes
{
    /// <summary>A single message of the conversation.</summary>
    public const string Message = "message";

    /// <summary>A committed version of the conversation's metadata.</summary>
    public const string Commit = "commit";

    /// <summary>A rolling summary as of one context epoch.</summary>
    public const string Summary = "summary";
}

/// <summary>
/// Base of every document in a conversation partition. All three kinds are written once and
/// never modified — the store issues inserts only.
/// </summary>
/// <remarks>
/// Both Newtonsoft and System.Text.Json attributes are present because the Cosmos SDK
/// serializes with Newtonsoft unless the application installs a custom <c>CosmosSerializer</c>.
/// </remarks>
public abstract class CosmosConversationDocument
{
    /// <summary>Unique within the partition, and shaped so ids sort in write order.</summary>
    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Partition key — the conversation id.</summary>
    [JsonProperty("cid")]
    [JsonPropertyName("cid")]
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>One of <see cref="CosmosConversationDocumentTypes"/>.</summary>
    [JsonProperty("type")]
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("ttl", NullValueHandling = NullValueHandling.Ignore)]
    [JsonPropertyName("ttl")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeToLiveSeconds { get; set; }
}

/// <summary>
/// One committed turn: the conversation's metadata as of <see cref="Version"/>. Inserting it
/// <em>is</em> the commit — the id is <c>v-{version}</c>, so a second writer attempting the
/// same version gets a 409 instead of overwriting. That replaces ETag concurrency entirely.
/// </summary>
public sealed class CosmosConversationCommit : CosmosConversationDocument
{
    [JsonProperty("version")]
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>Messages committed as of this version; reads never look past it.</summary>
    [JsonProperty("messageCount")]
    [JsonPropertyName("messageCount")]
    public int MessageCount { get; set; }

    /// <summary>Messages compacted away; reads start here.</summary>
    [JsonProperty("archived")]
    [JsonPropertyName("archived")]
    public int ArchivedCount { get; set; }

    /// <summary>Context epoch, which also identifies the summary document to read.</summary>
    [JsonProperty("epoch")]
    [JsonPropertyName("epoch")]
    public int ContextEpoch { get; set; }

    [JsonProperty("lastInputTokens")]
    [JsonPropertyName("lastInputTokens")]
    public long? LastInputTokenCount { get; set; }
}

/// <summary>One message, written once. The id encodes its ordinal, making appends idempotent.</summary>
public sealed class CosmosConversationMessage : CosmosConversationDocument
{
    [JsonProperty("ordinal")]
    [JsonPropertyName("ordinal")]
    public int Ordinal { get; set; }

    /// <summary>The serialized <c>ChatMessage</c>, preserving tool call and result content.</summary>
    [JsonProperty("message")]
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// The rolling summary as of one context epoch, written only when compaction produces a new
/// one — so an unchanged summary is never rewritten on an ordinary turn.
/// </summary>
public sealed class CosmosConversationSummary : CosmosConversationDocument
{
    [JsonProperty("epoch")]
    [JsonPropertyName("epoch")]
    public int ContextEpoch { get; set; }

    [JsonProperty("summary")]
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }
}
