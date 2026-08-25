using Trellis.State;

namespace Trellis.Graph.Leasing;

/// <summary>
/// A cross-instance <see cref="IRunLease"/> over any compare-and-swap capable
/// <see cref="ISharedStateStore"/> — so one implementation covers Redis, Cosmos DB, and
/// anything else behind that abstraction.
/// </summary>
/// <remarks>
/// <para><b>How exclusion works</b></para>
/// <para>
/// A lease is a key that only exists while someone holds it, created with a compare-and-swap
/// against "must not exist". Two instances racing both attempt the swap; the store makes one
/// of them lose. The key carries a time-to-live and the holder renews it in the background, so
/// an instance that dies releases the thread by simply going quiet — no reaper, no liveness
/// service, no operator.
/// </para>
/// <para><b>Why a separate counter</b></para>
/// <para>
/// The fencing token comes from an atomic counter under its own key, incremented once per
/// acquisition. It cannot live inside the lease value, because the lease value disappears when
/// the lease expires and the counter would restart — at which point a revived stale holder
/// could out-rank the instance that displaced it, which is the exact failure fencing exists to
/// prevent. The counter therefore outlives the lease.
/// </para>
/// <para>
/// ⚠ <b>Accepted limitation:</b> that counter is never expired, so a workload that mints
/// unbounded distinct thread ids accumulates one 8-byte key per thread id forever. This is
/// deliberate — expiring it would reintroduce the restart-at-zero hole. If your thread ids are
/// unbounded, prune counters with a backend-native TTL set far longer than any run could
/// conceivably stall (30 days is generous); past that horizon no paused holder can still be
/// revived, so restarting its counter is safe.
/// </para>
/// <para><b>Renewal and loss</b></para>
/// <para>
/// Renewal runs at a third of the lease duration, giving two chances to recover from a blip
/// before the lease could expire. A renewal that is refused means someone else holds the key
/// and the lease is lost at once. A renewal that throws — store unreachable — is retried until
/// the moment the lease would have expired anyway, and only then declared lost: by then
/// another instance can legitimately have taken the thread, so continuing to execute would be
/// the very thing this class exists to prevent.
/// </para>
/// </remarks>
public sealed class SharedStateRunLease : IRunLease
{
    /// <summary>Long enough to survive a GC pause or a brief store blip, short enough that a crashed instance frees the thread quickly.</summary>
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(30);

    private readonly IAtomicSharedStateStore _store;
    private readonly TimeSpan _duration;
    private readonly string _ownerId;
    private readonly TimeProvider _timeProvider;

    /// <param name="store">
    /// A store that can compare-and-swap. The type is required rather than tested for at run
    /// time: a lease over a read-modify-write store would hand the same thread to two callers,
    /// so this is a compile-time error rather than a runtime surprise.
    /// </param>
    /// <param name="leaseDuration">
    /// How long a lease survives without renewal. Lower means faster recovery after a crash;
    /// higher means more tolerance of pauses and store blips. Default 30 seconds.
    /// </param>
    /// <param name="ownerId">Identifies this holder in the stored value; defaults to machine, process, and a random suffix.</param>
    /// <param name="timeProvider">Injectable clock, for tests.</param>
    public SharedStateRunLease(
        IAtomicSharedStateStore store,
        TimeSpan? leaseDuration = null,
        string? ownerId = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        TimeSpan duration = leaseDuration ?? DefaultLeaseDuration;
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), duration, "A lease must last for a positive duration.");
        }

        _store = store;
        _duration = duration;
        _ownerId = ownerId ?? $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..8]}";
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<RunLeaseHandle?> TryAcquireAsync(
        string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);

        string leaseKey = LeaseKey(threadId);

        // A cheap read first: the contended case is "someone is already running this thread",
        // and answering it without burning a fencing token keeps the counter close to the
        // number of real acquisitions.
        if (await _store.GetAsync(leaseKey, cancellationToken).ConfigureAwait(false) is not null)
        {
            return null;
        }

        // Taken before the swap, so two racers never share a token even though only one wins.
        long token = await _store.IncrementAsync(FenceKey(threadId), cancellationToken).ConfigureAwait(false);
        string value = $"{_ownerId}:{token}";

        bool acquired = await _store
            .TrySetIfUnchangedAsync(leaseKey, expectedValue: null, value, _duration, cancellationToken)
            .ConfigureAwait(false);

        return acquired ? new Handle(this, threadId, token, value) : null;
    }

    private static string LeaseKey(string threadId) => $"trellis:graph:lease:{threadId}";

    private static string FenceKey(string threadId) => $"trellis:graph:fence:{threadId}";

    private sealed class Handle : RunLeaseHandle
    {
        private readonly SharedStateRunLease _lease;
        private readonly string _value;
        private readonly CancellationTokenSource _lost = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _renewal;

        public Handle(SharedStateRunLease lease, string threadId, long fencingToken, string value)
            : base(threadId, fencingToken)
        {
            _lease = lease;
            _value = value;
            _renewal = Task.Run(RenewAsync);
        }

        public override CancellationToken Lost => _lost.Token;

        private async Task RenewAsync()
        {
            TimeSpan period = _lease._duration / 3;
            DateTimeOffset validUntil = _lease._timeProvider.GetUtcNow() + _lease._duration;

            using var timer = new PeriodicTimer(period, _lease._timeProvider);
            try
            {
                while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
                {
                    bool renewed;
                    try
                    {
                        // Re-setting the same value refreshes the time-to-live, and the
                        // compare makes it a no-op if we are no longer the holder.
                        renewed = await _lease._store
                            .TrySetIfUnchangedAsync(
                                LeaseKey(ThreadId), _value, _value, _lease._duration, _stopping.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                    {
                        return;
                    }
                    catch
                    {
                        // The store is unreachable. Keep trying while the lease we already hold
                        // is still valid; give up the moment it is not.
                        if (_lease._timeProvider.GetUtcNow() >= validUntil)
                        {
                            await _lost.CancelAsync().ConfigureAwait(false);
                            return;
                        }
                        continue;
                    }

                    if (!renewed)
                    {
                        await _lost.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                    validUntil = _lease._timeProvider.GetUtcNow() + _lease._duration;
                }
            }
            catch (OperationCanceledException)
            {
                // Disposed; the release path takes it from here.
            }
        }

        public override async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
            try
            {
                await _renewal.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: cancelling the renewal loop is how it is stopped.
            }

            try
            {
                // Compare before removing, so a lease we have already lost is not deleted out
                // from under whoever holds it now. The window between the compare and the
                // delete is real but harmless: the token the next holder carries is higher
                // than ours, so a fenced checkpointer refuses anything we might still write.
                bool stillOurs = await _lease._store
                    .TrySetIfUnchangedAsync(LeaseKey(ThreadId), _value, _value, TimeSpan.FromSeconds(1))
                    .ConfigureAwait(false);
                if (stillOurs)
                {
                    await _lease._store.RemoveAsync(LeaseKey(ThreadId)).ConfigureAwait(false);
                }
            }
            catch
            {
                // The store is unreachable. The one-second time-to-live set just above — or
                // failing that the original duration — releases the thread without us.
            }

            _lost.Dispose();
            _stopping.Dispose();
        }
    }
}
