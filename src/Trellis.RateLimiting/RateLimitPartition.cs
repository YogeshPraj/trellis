namespace Trellis.RateLimiting;

/// <summary>What a request is limited by.</summary>
/// <param name="SubjectId">Who is calling; null when limits are not per-caller.</param>
/// <param name="ModelId">The model being called; null when limits are not per-model.</param>
public readonly record struct RateLimitPartitionKey(string? SubjectId, string? ModelId)
{
    /// <summary>Stable string form, used as the partition key of the underlying limiter.</summary>
    public override string ToString() => (SubjectId ?? "*") + "|" + (ModelId ?? "*");
}
