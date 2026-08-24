namespace Trellis.Credits;

/// <summary>
/// The operator-facing view of a subject's credits: grant them, read the balance, inspect the
/// ledger. Spending goes through an <see cref="ICreditAdmissionPolicy"/> instead, so nothing
/// can quietly deduct without an admission decision behind it.
/// </summary>
public sealed class CreditAccount(ICreditLedger ledger, TimeProvider? timeProvider = null)
{
    private readonly ICreditLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Micro-credits currently available. Negative under a post-charge policy that overshot.</summary>
    public ValueTask<long> GetBalanceAsync(string subjectId, CancellationToken cancellationToken = default) =>
        _ledger.GetBalanceAsync(subjectId, cancellationToken);

    /// <summary>
    /// Adds credits. <paramref name="grantId"/> is the idempotency key — reuse it for a
    /// recurring grant ("plan-2026-08") and a retried or duplicated top-up cannot double-credit.
    /// Returns false when that grant was already applied.
    /// </summary>
    public ValueTask<bool> GrantAsync(
        string subjectId,
        long microCredits,
        string grantId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectId);
        ArgumentException.ThrowIfNullOrEmpty(grantId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(microCredits);

        return _ledger.TryAppendAsync(
            new CreditEntry(grantId, subjectId, microCredits, CreditEntryKind.Grant, _time.GetUtcNow(), reason),
            cancellationToken);
    }

    /// <summary>
    /// Removes credits — a correction, or an expiry sweep. Also idempotent by
    /// <paramref name="adjustmentId"/>.
    /// </summary>
    public ValueTask<bool> DeductAsync(
        string subjectId,
        long microCredits,
        string adjustmentId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectId);
        ArgumentException.ThrowIfNullOrEmpty(adjustmentId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(microCredits);

        return _ledger.TryAppendAsync(
            new CreditEntry(adjustmentId, subjectId, -microCredits, CreditEntryKind.Grant, _time.GetUtcNow(), reason),
            cancellationToken);
    }

    /// <summary>Recent ledger entries, newest first, for audit and support.</summary>
    public ValueTask<IReadOnlyList<CreditEntry>> GetHistoryAsync(
        string subjectId, int limit = 100, CancellationToken cancellationToken = default) =>
        _ledger.GetEntriesAsync(subjectId, limit, cancellationToken);
}
