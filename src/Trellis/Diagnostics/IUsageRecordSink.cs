namespace Trellis.Diagnostics;

/// <summary>
/// Where usage records are kept (Repository). Implementations must not throw for a routine
/// failure — a request that succeeded must not be reported as failed because its audit row
/// could not be written.
/// </summary>
public interface IUsageRecordSink
{
    ValueTask RecordAsync(UsageRecord record, CancellationToken cancellationToken = default);
}
