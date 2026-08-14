using Trellis.Conversations;
using Trellis.State;

namespace Trellis.Conversations.Storage;

/// <summary>Options for <see cref="TieredConversationStore"/>.</summary>
public sealed class TieredConversationStoreOptions
{
    /// <summary>
    /// Whether replicas are written before <c>SaveAsync</c> returns, or handed to a
    /// background flusher (default <see cref="ReplicationMode.WriteThrough"/>).
    /// </summary>
    public ReplicationMode ReplicationMode { get; init; } = ReplicationMode.WriteThrough;

    /// <summary>
    /// How often the background flusher drains pending replications in
    /// <see cref="ReplicationMode.WriteBehind"/> (default 1s). This interval is also the
    /// floor on how much work an unexpected process death can lose.
    /// </summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Ceiling on conversations awaiting background replication (default 10,000). Pending
    /// writes are coalesced per conversation, so this bounds *active conversations*, not
    /// turns. Past the ceiling a save flushes inline rather than growing without bound —
    /// backpressure, not silent loss.
    /// </summary>
    public int MaxPendingReplications { get; init; } = 10_000;

    /// <summary>
    /// Called when a background replication fails after the write already returned success
    /// to the caller. This is the only place such a failure can surface — wire it up, or
    /// durability problems are invisible.
    /// </summary>
    public Action<string, Exception>? OnReplicationFailed { get; init; }

    /// <summary>How long a failed tier is skipped before it is retried (default 30s).</summary>
    public TimeSpan UnhealthyCooldown { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Ceiling for the cooldown, which doubles on each consecutive failure (default 5 min).</summary>
    public TimeSpan MaxUnhealthyCooldown { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>What to do when the authoritative tier is down (default: fail the save).</summary>
    public AuthorityUnavailableBehavior OnAuthorityUnavailable { get; init; } = AuthorityUnavailableBehavior.Fail;

    /// <summary>
    /// Repopulate faster tiers when a read falls through to a slower one (default true).
    /// </summary>
    public bool BackfillOnRead { get; init; } = true;

    /// <summary>Namespace for conversation keys.</summary>
    public string KeyPrefix { get; init; } = "conversation:";

    /// <summary>Called when a tier starts failing, so a degraded chain is loud rather than silent.</summary>
    public Action<string, Exception>? OnTierUnhealthy { get; init; }

    /// <summary>Called when a tier passes a probe and is readmitted to the chain.</summary>
    public Action<string>? OnTierRecovered { get; init; }

    /// <summary>
    /// How many conversations to remember as "repaired" per recovering tier before the tier
    /// is trusted wholesale (default 1024). Bounds the recovery bookkeeping.
    /// </summary>
    public int RepairTrackingCapacity { get; init; } = 1024;
}
