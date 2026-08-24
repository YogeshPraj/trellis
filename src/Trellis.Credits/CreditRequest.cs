namespace Trellis.Credits;

/// <summary>What an admission policy knows before a call is made.</summary>
/// <param name="SubjectId">Whose balance pays for it.</param>
/// <param name="RequestId">Idempotency key for the resulting ledger entries.</param>
/// <param name="ModelId">The model about to be called.</param>
/// <param name="EstimatedInputTokens">Estimated prompt size.</param>
/// <param name="MaxOutputTokens">
/// The caller's output cap. Without one a prepaid policy cannot bound the cost of a request,
/// which is the whole basis of a reservation.
/// </param>
public sealed record CreditRequest(
    string SubjectId,
    string RequestId,
    string? ModelId,
    long EstimatedInputTokens,
    long? MaxOutputTokens);
