namespace Trellis.Credits;

/// <summary>
/// In-process ledger for tests and single-instance apps. Enforces the same idempotency rule
/// as a distributed one, so code written against it behaves the same on a real backend.
/// </summary>
public sealed class InMemoryCreditLedger : ICreditLedger
{
    private readonly Dictionary<string, List<CreditEntry>> _bySubject = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public ValueTask<long> GetBalanceAsync(string subjectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectId);
        lock (_lock)
        {
            long balance = 0;
            if (_bySubject.TryGetValue(subjectId, out List<CreditEntry>? entries))
            {
                foreach (CreditEntry entry in entries)
                {
                    balance += entry.Amount;
                }
            }
            return ValueTask.FromResult(balance);
        }
    }

    public ValueTask<bool> TryAppendAsync(CreditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            if (!_ids.Add(entry.Id))
            {
                return ValueTask.FromResult(false);
            }
            if (!_bySubject.TryGetValue(entry.SubjectId, out List<CreditEntry>? entries))
            {
                _bySubject[entry.SubjectId] = entries = [];
            }
            entries.Add(entry);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<IReadOnlyList<CreditEntry>> GetEntriesAsync(
        string subjectId, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectId);
        lock (_lock)
        {
            if (!_bySubject.TryGetValue(subjectId, out List<CreditEntry>? entries))
            {
                return ValueTask.FromResult<IReadOnlyList<CreditEntry>>([]);
            }
            return ValueTask.FromResult<IReadOnlyList<CreditEntry>>(
                [.. entries.AsEnumerable().Reverse().Take(limit)]);
        }
    }
}
