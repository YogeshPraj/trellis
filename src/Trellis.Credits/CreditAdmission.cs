namespace Trellis.Credits;

/// <summary>An admission policy's verdict on a request.</summary>
/// <param name="IsAllowed">Whether the call may proceed.</param>
/// <param name="Reason">Why it was refused; null when allowed.</param>
/// <param name="ReservationId">The hold to settle against, when one was taken.</param>
/// <param name="ReservedCredits">Micro-credits held; zero when nothing was reserved.</param>
public sealed record CreditAdmission(
    bool IsAllowed,
    string? Reason = null,
    string? ReservationId = null,
    long ReservedCredits = 0)
{
    /// <summary>Allowed without a hold — the post-charge path.</summary>
    public static CreditAdmission Allowed { get; } = new(true);

    /// <summary>Refused, with a reason the caller can surface.</summary>
    public static CreditAdmission Denied(string reason) => new(false, reason);
}
