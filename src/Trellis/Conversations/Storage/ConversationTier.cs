using Trellis.Conversations;

namespace Trellis.Conversations.Storage;

/// <summary>One rung of a <see cref="TieredConversationStore"/> chain.</summary>
/// <param name="Name">Identifies the tier in health callbacks and errors.</param>
/// <param name="Store">The backing provider (Redis, an <c>IDistributedCache</c> bridge, in-memory, ...).</param>
/// <param name="TimeToLive">
/// Expiry for entries in this tier. Strongly recommended on accelerator tiers: it bounds how
/// long a stale entry can survive if this tier ever misses a write without being detected.
/// The authoritative (last) tier usually wants null — it is the durable copy.
/// </param>
public sealed record ConversationTier(string Name, IReplicatedConversationStore Store, TimeSpan? TimeToLive = null);
