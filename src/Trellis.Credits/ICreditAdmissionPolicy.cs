using Microsoft.Extensions.AI;

namespace Trellis.Credits;

/// <summary>
/// Decides whether a request may spend, and records what it actually spent (Strategy).
/// </summary>
/// <remarks>
/// An LLM's cost is unknown until after the call, which makes the obvious design — check the
/// balance, call, then deduct — unsafe: concurrent requests all see a sufficient balance and
/// all proceed. The two supported answers are
/// <see cref="PostChargeCreditPolicy"/> (accept bounded overspend, keep it simple) and
/// <see cref="PrepaidCreditPolicy"/> (hold an upper bound first, settle the difference after).
/// </remarks>
public interface ICreditAdmissionPolicy
{
    /// <summary>Called before the model request.</summary>
    ValueTask<CreditAdmission> AdmitAsync(CreditRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called after the request finishes, successfully or not. Must be called for every
    /// admitted request — a prepaid hold that is never settled stays held until it expires.
    /// </summary>
    ValueTask SettleAsync(
        CreditRequest request,
        CreditAdmission admission,
        UsageDetails? usage,
        CancellationToken cancellationToken = default);
}
