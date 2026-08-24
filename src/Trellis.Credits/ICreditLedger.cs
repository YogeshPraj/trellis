namespace Trellis.Credits;

/// <summary>
/// Append-only credit storage (Repository). Implementations must make
/// <see cref="TryAppendAsync"/> reject a duplicate id, because that is what makes a retried
/// charge safe.
/// </summary>
public interface ICreditLedger
{
    /// <summary>Current balance in micro-credits; may be negative under a post-charge policy.</summary>
    ValueTask<long> GetBalanceAsync(string subjectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends an entry. Returns false when <see cref="CreditEntry.Id"/> already exists —
    /// the entry was already recorded, which is success from the caller's point of view, not
    /// an error to retry.
    /// </summary>
    ValueTask<bool> TryAppendAsync(CreditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Entries for a subject, newest first, for audit and reconciliation.</summary>
    ValueTask<IReadOnlyList<CreditEntry>> GetEntriesAsync(
        string subjectId, int limit = 100, CancellationToken cancellationToken = default);
}
