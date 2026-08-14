using Trellis.Conversations;
using Trellis.State;

namespace Trellis.Conversations.Storage;

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

        /// <summary>
        /// When repair mode can end on the clock rather than on a guess: once the tier's
        /// TimeToLive has elapsed since recovery, no entry written before the outage can
        /// still exist, so the tier is provably free of stale data. Null for tiers without
        /// a TTL, which fall back to the capacity backstop.
        /// </summary>
        public DateTimeOffset? RepairingUntil;

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
            return !IsRepairingCore(tier) || _state[tier].Repaired.Contains(conversationId);
        }
    }

    /// <summary>
    /// Whether the tier may still be holding entries from before its outage. Ends on the
    /// clock when the tier has a TTL (nothing older can survive it), otherwise when enough
    /// conversations have been repaired to bound the bookkeeping.
    /// </summary>
    private bool IsRepairingCore(int tier)
    {
        TierState state = _state[tier];
        if (!state.Repairing)
        {
            return false;
        }
        if (state.RepairingUntil is DateTimeOffset until && _time.GetUtcNow() >= until)
        {
            state.Repairing = false;
            state.RepairingUntil = null;
            state.Repaired.Clear();
            return false;
        }
        return true;
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
        state.RepairingUntil = _tiers[tier].TimeToLive is TimeSpan ttl ? _time.GetUtcNow() + ttl : null;
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
            state.RepairingUntil = null;   // set when the cooldown expires and recovery starts
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
            if (!IsRepairingCore(tier))
            {
                return;
            }
            TierState state = _state[tier];
            state.Repaired.Add(conversationId);

            // Memory backstop for tiers configured without a TTL, where repair mode has no
            // clock to end on. This one is a bound, not a proof: a conversation untouched
            // since the outage could still read stale afterwards — give accelerator tiers a
            // TimeToLive to close that properly.
            if (state.RepairingUntil is null && state.Repaired.Count >= _options.RepairTrackingCapacity)
            {
                state.Repairing = false;
                state.Repaired.Clear();
            }
        }
    }
}
