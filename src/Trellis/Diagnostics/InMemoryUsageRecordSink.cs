namespace Trellis.Diagnostics;

/// <summary>
/// Keeps the most recent records in memory, for tests and small single-instance apps.
/// Bounded, because an unbounded audit buffer is a memory leak with good intentions.
/// </summary>
/// <param name="capacity">How many records to retain; the oldest are dropped past this.</param>
public sealed class InMemoryUsageRecordSink(int capacity = 10_000) : IUsageRecordSink
{
    private readonly Queue<UsageRecord> _records = new();
    private readonly Lock _lock = new();

    /// <summary>Retained records, oldest first.</summary>
    public IReadOnlyList<UsageRecord> Records
    {
        get
        {
            lock (_lock)
            {
                return [.. _records];
            }
        }
    }

    public ValueTask RecordAsync(UsageRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            _records.Enqueue(record);
            while (_records.Count > capacity)
            {
                _records.Dequeue();
            }
        }
        return ValueTask.CompletedTask;
    }
}
