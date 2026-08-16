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
public sealed class TieredConversationStore : IConversationStore, IAsyncDisposable
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

    /// <summary>A conversation awaiting background replication; newer versions replace older.</summary>
    private sealed record PendingWrite(string Key, string ConversationId, int Version, string Json);

    private readonly IReadOnlyList<ConversationTier> _tiers;
    private readonly TierState[] _state;
    private readonly TieredConversationStoreOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _healthLock = new();

    // Coalesced by conversation id: a chatty session costs one replication, not one per turn,
    // and the map is bounded by active conversations rather than by traffic.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingWrite> _pending = new();
    private readonly CancellationTokenSource _flusherShutdown = new();
    private readonly Task? _flusher;
    private int _disposed;

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

        if (_options.ReplicationMode == ReplicationMode.WriteBehind && _tiers.Count > 1)
        {
            _flusher = RunFlusherAsync(_flusherShutdown.Token);
        }
    }

    public TieredConversationStore(params ConversationTier[] tiers)
        : this((IReadOnlyList<ConversationTier>)tiers)
    {
    }

    /// <summary>
    /// The tier that owns version checking: the last (durable) tier under write-through, the
    /// first under write-behind — in both cases, the one written synchronously.
    /// </summary>
    public string AuthorityName =>
        _tiers[_options.ReplicationMode == ReplicationMode.WriteBehind ? 0 : ^1].Name;

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
                await BackfillAsync(key, conversationId, snapshot.Version, json, upToTier: i, cancellationToken)
                    .ConfigureAwait(false);
            }
            return ConversationSerializer.ToConversation(snapshot);
        }
        return null;
    }

    public async ValueTask SaveAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        // Without this, a write-behind save after disposal would queue into a map nothing
        // will ever drain — the caller would be told it saved, and it never would.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        string key = _options.KeyPrefix + conversation.Id;

        int authority = ResolveAuthority();
        (int next, string json) = await WriteAuthorityAsync(authority, key, conversation, cancellationToken)
            .ConfigureAwait(false);
        conversation.MarkPersisted(next);

        if (_options.ReplicationMode == ReplicationMode.WriteBehind)
        {
            QueueReplication(new PendingWrite(key, conversation.Id, next, json));

            // Past the ceiling, flush inline: backpressure on this caller is honest, and
            // beats a queue that grows until the process dies.
            if (_pending.Count > _options.MaxPendingReplications)
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        // The authority has accepted; every other tier is now a replica to bring in line.
        // They are independent backends, so writing them concurrently costs the slowest
        // replica rather than the sum of all of them.
        await ReplicateToOthersAsync(authority, key, conversation.Id, next, json, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a snapshot to every healthy tier except <paramref name="skipTier"/>, concurrently.
    /// Returns false if any target did not end up holding it — because it was cooling down, or
    /// because the write failed — so a background flush knows to try again.
    /// </summary>
    private async ValueTask<bool> ReplicateToOthersAsync(
        int skipTier, string key, string conversationId, int version, string json, CancellationToken cancellationToken)
    {
        List<Task<bool>>? replicas = null;
        bool allApplied = true;
        for (int i = 0; i < _tiers.Count; i++)
        {
            if (i == skipTier)
            {
                continue;
            }
            if (IsCoolingDown(i))
            {
                allApplied = false;     // skipped, so this tier is still behind
                continue;
            }
            (replicas ??= []).Add(ReplicateAsync(i, key, conversationId, version, json, cancellationToken).AsTask());
        }
        if (replicas is not null)
        {
            bool[] results = await Task.WhenAll(replicas).ConfigureAwait(false);
            allApplied &= results.All(applied => applied);
        }
        return allApplied;
    }

    /// <summary>Records the newest snapshot for a conversation, discarding any older pending one.</summary>
    private void QueueReplication(PendingWrite write) =>
        _pending.AddOrUpdate(
            write.ConversationId,
            write,
            (_, existing) => existing.Version >= write.Version ? existing : write);

    /// <summary>Conversations currently awaiting background replication.</summary>
    public int PendingReplicationCount => _pending.Count;

    /// <summary>
    /// Drains every pending background replication. Call before shutdown — disposal does it
    /// too — so a graceful stop loses nothing.
    /// </summary>
    /// <remarks>
    /// One pass over what is pending right now. Writes that could not be applied re-queue
    /// themselves for the next tick rather than being retried in a loop here — looping would
    /// spin forever while a target tier is down.
    /// </remarks>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        List<Task>? writes = null;
        foreach (string id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out PendingWrite? write))
            {
                (writes ??= []).Add(FlushOneAsync(write, cancellationToken));
            }
        }
        if (writes is not null)
        {
            await Task.WhenAll(writes).ConfigureAwait(false);
        }
    }

    private async Task FlushOneAsync(PendingWrite write, CancellationToken cancellationToken)
    {
        bool applied;
        try
        {
            // Tier 0 already has it — it was written synchronously.
            applied = await ReplicateToOthersAsync(
                skipTier: 0, write.Key, write.ConversationId, write.Version, write.Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The caller was already told the save succeeded, so this is the only place such
            // a failure can surface.
            _options.OnReplicationFailed?.Invoke(write.ConversationId, ex);
            applied = false;
        }

        if (!applied)
        {
            // Put it back so the next tick retries. Coalescing means this cannot grow the
            // map, and a newer turn simply supersedes it — without this, a turn that failed
            // to replicate would be lost from the durable tier for good once the
            // conversation went idle.
            QueueReplication(write);
        }
    }

    private async Task RunFlusherAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_options.FlushInterval, _time, cancellationToken).ConfigureAwait(false);
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down; DisposeAsync performs the final flush.
        }
    }

    /// <summary>Stops the background flusher after draining whatever it still holds.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _flusherShutdown.CancelAsync().ConfigureAwait(false);
        if (_flusher is not null)
        {
            try
            {
                await _flusher.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        _flusherShutdown.Dispose();
    }

    public async ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        string key = _options.KeyPrefix + conversationId;
        List<Exception>? failures = null;

        // Drop any queued replication first: a pending write flushed after the tiers were
        // cleared would put the conversation back, resurrecting what the caller deleted.
        _pending.TryRemove(conversationId, out _);

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

        // A save racing this delete could have re-queued between the two; clear it again so
        // the flusher cannot revive the conversation after we reported it gone.
        _pending.TryRemove(conversationId, out _);

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
    /// compare-and-swap. Returns the version it accepted together with the serialized
    /// snapshot, so replicas reuse it instead of re-serializing the whole conversation.
    /// </summary>
    private async ValueTask<(int Version, string Json)> WriteAuthorityAsync(
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
            return (next, json);
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
    /// <remarks>
    /// The write is version-conditional. Two instances replicating concurrently — or a
    /// background flush landing late — could otherwise put an older snapshot on top of a
    /// newer one, leaving the replica stale while looking healthy.
    /// </remarks>
    private async ValueTask<bool> ReplicateAsync(
        int tier, string key, string conversationId, int version, string json, CancellationToken cancellationToken)
    {
        try
        {
            string? current = await _tiers[tier].Store.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (current is not null && ConversationSerializer.Deserialize(current)?.Version >= version)
            {
                MarkHealthy(tier);
                MarkRepaired(tier, conversationId);
                return true;   // a newer snapshot already landed here
            }

            await _tiers[tier].Store
                .SetAsync(key, json, _tiers[tier].TimeToLive, cancellationToken)
                .ConfigureAwait(false);
            MarkHealthy(tier);
            MarkRepaired(tier, conversationId);
            return true;
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
            return false;
        }
    }

    private async ValueTask BackfillAsync(
        string key, string conversationId, int version, string json, int upToTier, CancellationToken cancellationToken)
    {
        List<Task>? writes = null;
        for (int i = 0; i < upToTier; i++)
        {
            if (IsCoolingDown(i))
            {
                continue;
            }
            (writes ??= []).Add(ReplicateAsync(i, key, conversationId, version, json, cancellationToken).AsTask());
        }
        if (writes is not null)
        {
            await Task.WhenAll(writes).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The tier that owns version checking. Write-through puts it on the last (most durable)
    /// tier; write-behind puts it on the first, because that is the only tier guaranteed
    /// current when the save returns. Either way it is the synchronously-written tier.
    /// </summary>
    private int ResolveAuthority()
    {
        int preferred = _options.ReplicationMode == ReplicationMode.WriteBehind ? 0 : _tiers.Count - 1;
        if (!IsCoolingDown(preferred) || _options.OnAuthorityUnavailable == AuthorityUnavailableBehavior.Fail)
        {
            // When configured to fail, still return the preferred tier: failing the save is
            // the intended outcome, and probing keeps recovery immediate.
            return preferred;
        }

        // Promote the nearest healthy tier, searching away from the preferred position.
        if (_options.ReplicationMode == ReplicationMode.WriteBehind)
        {
            for (int i = 1; i < _tiers.Count; i++)
            {
                if (!IsCoolingDown(i))
                {
                    return i;
                }
            }
        }
        else
        {
            for (int i = preferred - 1; i >= 0; i--)
            {
                if (!IsCoolingDown(i))
                {
                    return i;
                }
            }
        }
        return preferred;
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
