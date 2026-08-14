using Trellis.Conversations;
using Trellis.State;

namespace Trellis.Conversations.Storage;

/// <summary>Options for <see cref="TieredConversationStore"/>.</summary>
public sealed class TieredConversationStoreOptions
{
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
