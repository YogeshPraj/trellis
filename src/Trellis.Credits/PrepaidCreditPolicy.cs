using Microsoft.Extensions.AI;

namespace Trellis.Credits;

/// <summary>
/// Holds an upper bound on the cost before the call, then settles the difference once the
/// real usage is known. A subject cannot spend credits it does not have, even under
/// concurrency.
/// </summary>
/// <remarks>
/// <para>
/// The reservation is itself a ledger entry, so a hold is visible in the balance immediately
/// and the whole thing stays append-only and auditable. Settlement appends a release for the
/// unused portion rather than editing the reservation.
/// </para>
/// <para>
/// The bound comes from <see cref="CreditRequest.MaxOutputTokens"/>. A request without one
/// has no computable ceiling: <paramref name="defaultMaxOutputTokens"/> is assumed, and if
/// that is null the request is refused rather than admitted against an unknown cost.
/// </para>
/// <para>
/// ⚠ A hold whose request dies before settling stays held. Call
/// <see cref="ReleaseExpiredAsync"/> on a timer, or those credits are stranded until someone
/// notices.
/// </para>
/// </remarks>
public sealed class PrepaidCreditPolicy(
    ICreditLedger ledger,
    ICreditRateModel rates,
    long? defaultMaxOutputTokens = null,
    TimeSpan? reservationLifetime = null,
    TimeProvider? timeProvider = null) : ICreditAdmissionPolicy
{
    private readonly ICreditLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    private readonly ICreditRateModel _rates = rates ?? throw new ArgumentNullException(nameof(rates));
    private readonly TimeSpan _lifetime = reservationLifetime ?? TimeSpan.FromMinutes(10);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async ValueTask<CreditAdmission> AdmitAsync(
        CreditRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        long? maxOutput = request.MaxOutputTokens ?? defaultMaxOutputTokens;
        if (maxOutput is null)
        {
            return CreditAdmission.Denied(
                "Prepaid admission needs an output cap: set MaxOutputTokens on the request, or a " +
                "default on the policy. Without one the maximum cost of the call is unknown.");
        }

        // Price the worst case: the whole prompt uncached, and the output cap reached.
        var worstCase = new UsageDetails
        {
            InputTokenCount = request.EstimatedInputTokens,
            OutputTokenCount = maxOutput,
        };
        if (_rates.CreditsFor(request.ModelId, worstCase) is not long estimate)
        {
            return CreditAdmission.Denied($"Model '{request.ModelId}' has no price, so no bound can be held.");
        }

        long balance = await _ledger.GetBalanceAsync(request.SubjectId, cancellationToken).ConfigureAwait(false);
        if (balance < estimate)
        {
            return CreditAdmission.Denied($"Balance {balance} is below the {estimate} required for this request.");
        }

        string reservationId = request.RequestId + ":hold";
        bool held = await _ledger.TryAppendAsync(
            new CreditEntry(
                reservationId,
                request.SubjectId,
                -estimate,
                CreditEntryKind.Reservation,
                _time.GetUtcNow(),
                Reason: "prepaid-hold",
                request.ModelId,
                request.EstimatedInputTokens,
                maxOutput,
                ReservationId: reservationId),
            cancellationToken).ConfigureAwait(false);

        // A duplicate hold means this request was already admitted; re-admitting is safe,
        // and settling will still net out correctly against the single reservation.
        return new CreditAdmission(true, null, reservationId, estimate);
    }

    public async ValueTask SettleAsync(
        CreditRequest request,
        CreditAdmission admission,
        UsageDetails? usage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admission);
        if (admission.ReservationId is not string reservationId)
        {
            return;
        }

        long actual = usage is null ? 0 : _rates.CreditsFor(request.ModelId, usage) ?? 0;

        // Release the difference. When the call overshot the estimate the difference is
        // negative, which charges the excess rather than refunding it — the ledger stays the
        // arithmetic truth either way.
        long release = admission.ReservedCredits - actual;

        await _ledger.TryAppendAsync(
            new CreditEntry(
                reservationId + ":settle",
                request.SubjectId,
                release,
                CreditEntryKind.ReservationRelease,
                _time.GetUtcNow(),
                Reason: "prepaid-settle",
                request.ModelId,
                usage?.InputTokenCount,
                usage?.OutputTokenCount,
                ReservationId: reservationId),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns credits held by reservations older than the configured lifetime that were
    /// never settled — the crashed-request case. Run it periodically.
    /// </summary>
    public async ValueTask<int> ReleaseExpiredAsync(
        string subjectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectId);
        DateTimeOffset cutoff = _time.GetUtcNow() - _lifetime;

        IReadOnlyList<CreditEntry> entries = await _ledger
            .GetEntriesAsync(subjectId, int.MaxValue, cancellationToken).ConfigureAwait(false);

        HashSet<string> settled = [.. entries
            .Where(e => e.Kind == CreditEntryKind.ReservationRelease && e.ReservationId is not null)
            .Select(e => e.ReservationId!)];

        int released = 0;
        foreach (CreditEntry hold in entries.Where(e =>
            e.Kind == CreditEntryKind.Reservation && e.At <= cutoff && !settled.Contains(e.Id)))
        {
            bool appended = await _ledger.TryAppendAsync(
                new CreditEntry(
                    hold.Id + ":expired",
                    subjectId,
                    -hold.Amount,           // the hold was negative; give it back
                    CreditEntryKind.ReservationRelease,
                    _time.GetUtcNow(),
                    Reason: "prepaid-expired",
                    ReservationId: hold.Id),
                cancellationToken).ConfigureAwait(false);
            if (appended)
            {
                released++;
            }
        }
        return released;
    }
}
