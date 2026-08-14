namespace Trellis.Conversations.Storage;

/// <summary>How a <see cref="TieredConversationStore"/> gets a turn into its slower tiers.</summary>
public enum ReplicationMode
{
    /// <summary>
    /// Every healthy tier is written before <c>SaveAsync</c> returns, and the <b>last</b>
    /// (most durable) tier owns the version check. A save costs the authority's round trip
    /// plus the slowest replica, and when it returns the turn is durably stored.
    /// </summary>
    WriteThrough,

    /// <summary>
    /// Only the <b>first</b> tier is written synchronously — it becomes the authority and
    /// owns the version check — and the remaining tiers are updated by a background flusher.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Saves cost one fast round trip, and an outage of the durable tier no longer fails a
    /// turn. The price is a real change of guarantee: <b>a returned save is not yet durable</b>.
    /// Everything written since the last flush lives only in the first tier, so losing that
    /// tier — or losing the process before it flushes — loses those turns.
    /// </para>
    /// <para>
    /// Pending writes are coalesced per conversation, so a chatty session costs one
    /// replication rather than one per turn. <c>FlushAsync</c> drains them on demand and
    /// disposal flushes automatically, so a graceful shutdown loses nothing; an abrupt kill
    /// loses up to <c>FlushInterval</c> of work.
    /// </para>
    /// </remarks>
    WriteBehind,
}
