namespace Trellis.Credits;

/// <summary>
/// A request was refused because the subject could not pay for it. Carries the policy's
/// reason so an API surface can tell the caller what to do about it.
/// </summary>
public sealed class InsufficientCreditsException(string subjectId, string reason)
    : Exception($"Subject '{subjectId}' cannot be admitted: {reason}")
{
    public string SubjectId { get; } = subjectId;

    /// <summary>The admission policy's explanation, suitable for surfacing to the caller.</summary>
    public string Reason { get; } = reason;
}
