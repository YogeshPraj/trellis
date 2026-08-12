using Trellis.State;

namespace Trellis;

/// <summary>One rung of a <see cref="TieredConversationStore"/> chain.</summary>
/// <param name="Name">Identifies the tier in health callbacks and errors.</param>
/// <param name="Store">The backing provider (Redis, an <c>IDistributedCache</c> bridge, in-memory, ...).</param>
/// <param name="TimeToLive">
/// Expiry for entries in this tier. Strongly recommended on accelerator tiers: it bounds how
/// long a stale entry can survive if this tier ever misses a write without being detected.
/// The authoritative (last) tier usually wants null — it is the durable copy.
/// </param>
public sealed record ConversationTier(string Name, ISharedStateStore Store, TimeSpan? TimeToLive = null);

/// <summary>What to do when the authoritative tier itself is unreachable.</summary>
public enum AuthorityUnavailableBehavior
{
    /// <summary>
    /// Fail the save (default). Nothing is written anywhere, so no tier can accumulate turns
    /// the durable copy will never see — the conversation cannot fork.
    /// </summary>
    Fail,

    /// <summary>
    /// Promote the highest-ranked healthy tier and keep serving. Turns survive the outage,
    /// but they exist only in a non-durable tier until the authority returns, and if that
    /// tier is also lost they are gone. Choose this only when continuing matters more than
    /// durability for the length of an outage.
    /// </summary>
    PromoteHealthiest,
}

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

/// <summary>
/// An ordered chain of conversation storage tiers — fastest first, durable last — written
/// through synchronously so every healthy tier holds the same version. Add as many tiers as
/// you like; the list is the configuration.
/// </summary>
/// <remarks>
/// <para><b>How it behaves</b></para>
/// <list type="bullet">
/// <item>The <b>last tier is authoritative</b>: it owns the version check and the
/// compare-and-swap, so concurrent writers are still detected exactly as with a single store.</item>
/// <item>Every other tier is a <b>replica</b>, written unconditionally after the authority
/// accepts. A replica write that fails never fails the turn — the tier is marked unhealthy
/// and its entry is deleted, so it can never serve data older than the authority.</item>
/// <item>Reads take the first healthy tier that has the conversation and, by default,
/// backfill the tiers that missed it.</item>
/// <item>An unhealthy tier is skipped for reads and writes until its cooldown expires
/// (doubling per consecutive failure, capped), then retried automatically.</item>
/// <item>A recovering tier may hold pre-outage data, so it is <b>not trusted for reads</b>
/// until a write-through has repaired that specific conversation.</item>
/// </list>
/// <para><b>What this does not solve</b></para>
/// <para>
/// Health is tracked per process. If one instance's replica write fails, other instances do
/// not learn that the tier is stale, and could read a stale copy from it — the authority's
/// version check turns that into a rejected save rather than corruption, but the turn is
/// wasted. Set <see cref="ConversationTier.TimeToLive"/> on accelerator tiers to bound that
/// window, and prefer a tier whose own replication (Redis replicas, zone redundancy) makes
/// single-node failure invisible in the first place.
/// </para>
/// </remarks>
public sealed class TieredConversationStore : IConversationStore
{
    private sealed class TierState
    {
        public int ConsecutiveFailures;
        public DateTimeOffset? UnhealthyUntil;
        public bool Repairing;
        public HashSet<string> Repaired { get; } = new(StringComparer.Ordinal);
    }

    private readonly IReadOnlyList<ConversationTier> _tiers;
    private readonly TierState[] _state;
    private readonly TieredConversationStoreOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _healthLock = new();

    public TieredConversationStore(
        IReadOnlyList<ConversationTier> tiers,
        TieredConversationStoreOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        if (tiers.Count == 0)
        {
            throw new ArgumentException("At least one tier is required.", nameof(tiers));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ConversationTier tier in tiers)
        {
            ArgumentNullException.ThrowIfNull(tier);
            ArgumentNullException.ThrowIfNull(tier.Store);
            if (!names.Add(tier.Name))
            {
                throw new ArgumentException($"Duplicate tier name '{tier.Name}'.", nameof(tiers));
            }
        }

        _tiers = tiers;
        _state = [.. tiers.Select(_ => new TierState())];
        _options = options ?? new TieredConversationStoreOptions();
        _time = timeProvider ?? TimeProvider.System;
    }

    public TieredConversationStore(params ConversationTier[] tiers)
        : this((IReadOnlyList<ConversationTier>)tiers)
    {
    }

    /// <summary>The durable tier that owns version checking — the last one in the chain.</summary>
    public string AuthorityName => _tiers[^1].Name;

    /// <summary>Tier names currently skipped because they are cooling down after a failure.</summary>
    public IReadOnlyList<string> UnhealthyTiers
    {
        get
        {
            lock (_healthLock)
            {
                return [.. _tiers.Where((_, i) => IsCoolingDown(i)).Select(t => t.Name)];
            }
        }
    }

    public async ValueTask<Conversation?> LoadAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        string key = _options.KeyPrefix + conversationId;

        for (int i = 0; i < _tiers.Count; i++)
        {
            if (!CanRead(i, conversationId))
            {
                continue;
            }

            string? json;
            try
            {
                json = await _tiers[i].Store.GetAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                MarkUnhealthy(i, ex);
                continue;
            }

            if (json is null)
            {
                continue;   // miss: try the next tier down
            }

            MarkHealthy(i);
            ConversationSnapshot? snapshot = ConversationSerializer.Deserialize(json);
            if (snapshot is null)
            {
                continue;
            }

            if (_options.BackfillOnRead && i > 0)
            {
                await BackfillAsync(key, conversationId, json, upToTier: i, cancellationToken).ConfigureAwait(false);
            }
            return ConversationSerializer.ToConversation(snapshot);
        }
        return null;
    }

    public async ValueTask SaveAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        string key = _options.KeyPrefix + conversation.Id;

        int authority = ResolveAuthority();
        int next = await WriteAuthorityAsync(authority, key, conversation, cancellationToken).ConfigureAwait(false);

        // The authority has accepted; every other tier is now a replica to bring in line.
        string json = ConversationSerializer.Serialize(conversation, next);
        conversation.MarkPersisted(next);

        for (int i = 0; i < _tiers.Count; i++)
        {
            if (i == authority || IsCoolingDown(i))
            {
                continue;
            }
            await ReplicateAsync(i, key, conversation.Id, json, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        string key = _options.KeyPrefix + conversationId;
        List<Exception>? failures = null;

        for (int i = 0; i < _tiers.Count; i++)
        {
            try
            {
                await _tiers[i].Store.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                MarkUnhealthy(i, ex);
                (failures ??= []).Add(ex);
            }
        }

        // A delete that only partly succeeded would leave the conversation retrievable from a
        // tier that outlives the others — say so rather than reporting success.
        if (failures is not null)
        {
            throw new AggregateException(
                $"Conversation '{conversationId}' could not be deleted from every tier.", failures);
        }
    }

    /// <summary>
    /// Writes through the authoritative tier, which performs the version check and
    /// compare-and-swap. Returns the version it accepted.
    /// </summary>
    private async ValueTask<int> WriteAuthorityAsync(
        int authority, string key, Conversation conversation, CancellationToken cancellationToken)
    {
        ISharedStateStore store = _tiers[authority].Store;
        try
        {
            string? current = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
            int stored = current is null ? 0 : ConversationSerializer.Deserialize(current)?.Version ?? 0;
            if (stored != conversation.Version)
            {
                throw new ConversationConcurrencyException(conversation.Id, conversation.Version, stored);
            }

            int next = conversation.Version + 1;
            string json = ConversationSerializer.Serialize(conversation, next);

            if (store is IAtomicSharedStateStore atomic)
            {
                bool swapped = await atomic
                    .TrySetIfUnchangedAsync(key, current, json, _tiers[authority].TimeToLive, cancellationToken)
                    .ConfigureAwait(false);
                if (!swapped)
                {
                    throw new ConversationConcurrencyException(conversation.Id, conversation.Version, stored);
                }
            }
            else
            {
                await store.SetAsync(key, json, _tiers[authority].TimeToLive, cancellationToken).ConfigureAwait(false);
            }

            MarkHealthy(authority);
            MarkRepaired(authority, conversation.Id);
            return next;
        }
        catch (ConversationConcurrencyException)
        {
            throw;   // a real conflict, not a tier failure
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MarkUnhealthy(authority, ex);
            throw;
        }
    }

    /// <summary>
    /// Copies the accepted snapshot to a replica tier. Failure never fails the turn, but the
    /// tier's entry is removed so it can never serve a version older than the authority's.
    /// </summary>
    private async ValueTask ReplicateAsync(
        int tier, string key, string conversationId, string json, CancellationToken cancellationToken)
    {
        try
        {
            await _tiers[tier].Store
                .SetAsync(key, json, _tiers[tier].TimeToLive, cancellationToken)
                .ConfigureAwait(false);
            MarkHealthy(tier);
            MarkRepaired(tier, conversationId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MarkUnhealthy(tier, ex);
            try
            {
                await _tiers[tier].Store.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception cleanup) when (cleanup is not OperationCanceledException)
            {
                // The tier is already unhealthy and skipped for reads; TimeToLive is the
                // backstop that stops the orphaned entry outliving the outage.
            }
        }
    }

    private async ValueTask BackfillAsync(
        string key, string conversationId, string json, int upToTier, CancellationToken cancellationToken)
    {
        for (int i = 0; i < upToTier; i++)
        {
            if (IsCoolingDown(i))
            {
                continue;
            }
            await ReplicateAsync(i, key, conversationId, json, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The tier that owns version checking: the last one, unless it is cooling down and the
    /// caller opted into promotion.
    /// </summary>
    private int ResolveAuthority()
    {
        int last = _tiers.Count - 1;
        if (!IsCoolingDown(last))
        {
            return last;
        }
        if (_options.OnAuthorityUnavailable == AuthorityUnavailableBehavior.Fail)
        {
            // Retry it anyway: failing the save is the configured outcome, and probing keeps
            // recovery immediate instead of waiting out the cooldown.
            return last;
        }
        for (int i = last - 1; i >= 0; i--)
        {
            if (!IsCoolingDown(i))
            {
                return i;
            }
        }
        return last;
    }

    /// <summary>
    /// A tier serves reads when it is healthy and, if it is recovering from a failure, only
    /// for conversations a write-through has already repaired — its other entries predate the
    /// outage and may be stale.
    /// </summary>
    private bool CanRead(int tier, string conversationId)
    {
        lock (_healthLock)
        {
            if (IsCoolingDownCore(tier))
            {
                return false;
            }
            TierState state = _state[tier];
            return !state.Repairing || state.Repaired.Contains(conversationId);
        }
    }

    private bool IsCoolingDown(int tier)
    {
        lock (_healthLock)
        {
            return IsCoolingDownCore(tier);
        }
    }

    private bool IsCoolingDownCore(int tier)
    {
        TierState state = _state[tier];
        if (state.UnhealthyUntil is not DateTimeOffset until)
        {
            return false;
        }
        if (until > _time.GetUtcNow())
        {
            return true;
        }

        // Cooldown expired: readmit the tier, but treat its contents as suspect until
        // repaired, because it missed every write made while it was down.
        state.UnhealthyUntil = null;
        state.Repairing = true;
        state.Repaired.Clear();
        _options.OnTierRecovered?.Invoke(_tiers[tier].Name);
        return false;
    }

    private void MarkUnhealthy(int tier, Exception error)
    {
        bool notify = false;
        lock (_healthLock)
        {
            TierState state = _state[tier];
            state.ConsecutiveFailures++;
            double factor = Math.Pow(2, Math.Min(state.ConsecutiveFailures - 1, 16));
            TimeSpan cooldown = TimeSpan.FromTicks(Math.Min(
                (long)(_options.UnhealthyCooldown.Ticks * factor),
                _options.MaxUnhealthyCooldown.Ticks));
            state.UnhealthyUntil = _time.GetUtcNow() + cooldown;
            state.Repairing = true;
            state.Repaired.Clear();
            notify = true;
        }
        if (notify)
        {
            _options.OnTierUnhealthy?.Invoke(_tiers[tier].Name, error);
        }
    }

    private void MarkHealthy(int tier)
    {
        lock (_healthLock)
        {
            TierState state = _state[tier];
            state.ConsecutiveFailures = 0;
            state.UnhealthyUntil = null;
        }
    }

    private void MarkRepaired(int tier, string conversationId)
    {
        lock (_healthLock)
        {
            TierState state = _state[tier];
            if (!state.Repairing)
            {
                return;
            }
            state.Repaired.Add(conversationId);

            // Past the tracking budget, trust the tier wholesale rather than grow without
            // bound — by then it has served many repaired writes without failing.
            if (state.Repaired.Count >= _options.RepairTrackingCapacity)
            {
                state.Repairing = false;
                state.Repaired.Clear();
            }
        }
    }
}
